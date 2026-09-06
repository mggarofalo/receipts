using Application.Commands.ReceiptItem.Create;
using Application.Exceptions;
using Application.Interfaces.Services;
using Common;
using Domain;
using Domain.Core;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using FluentAssertions.Execution;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
public partial class TemplateNormalizationOwnershipTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Theory]
	[InlineData(1)]
	[InlineData(3)]
	public async Task RealItemCreateChain_PreservesTrustedTemplateIdentity_WithNoInventedScore(int count)
	{
		Seed seed = await SeedAsync();
		using ServiceProvider provider = BuildProvider(Mock.Of<INormalizedDescriptionService>());
		CreateReceiptItemCommandHandler handler = provider.GetRequiredService<CreateReceiptItemCommandHandler>();
		List<ReceiptItem> items = Enumerable.Range(0, count).Select(_ => NewItem(seed.Bread)).ToList();
		var created = await handler.Handle(new(items, seed.Receipt, Enumerable.Repeat<Guid?>(seed.Template, count).ToList()), CancellationToken.None);
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		List<ReceiptItemEntity> persisted = await verify.ReceiptItems.Where(item => item.ReceiptId == seed.Receipt).ToListAsync();
		using AssertionScope assertions = new();
		created.Should().HaveCount(count).And.OnlyContain(item => item.NormalizedDescriptionId == seed.Milk && item.NormalizedDescriptionMatchScore == null);
		persisted.Should().HaveCount(count).And.OnlyContain(item => item.NormalizedDescriptionId == seed.Milk && item.NormalizedDescriptionMatchScore == null);
		persisted.Should().OnlyContain(item => item.Description == "Entered receipt text" && item.Quantity == 2 && item.TotalAmount == 6);
	}

	[Theory]
	[InlineData("none")]
	[InlineData("unknown")]
	[InlineData("deleted")]
	[InlineData("unlinked")]
	public async Task MissingUsableTemplateHint_FallsBackToUnresolved_AndIgnoresCallerCanonicalMetadata(string hint)
	{
		Seed seed = await SeedAsync();
		if (hint is "deleted" or "unlinked")
		{
			await using ApplicationDbContext edit = fixture.CreateDbContext();
			ItemTemplateEntity template = await edit.ItemTemplates.SingleAsync(row => row.Id == seed.Template);
			if (hint == "deleted")
			{
				template.DeletedAt = DateTimeOffset.UtcNow;
			}
			else
			{
				template.NormalizedDescriptionId = null;
			}

			await edit.SaveChangesAsync();
		}
		using ServiceProvider provider = BuildProvider(Mock.Of<INormalizedDescriptionService>());
		List<Guid?>? hints = hint == "none" ? null : [hint == "unknown" ? Guid.NewGuid() : seed.Template];
		var created = await provider.GetRequiredService<CreateReceiptItemCommandHandler>().Handle(new([NewItem(seed.Bread)], seed.Receipt, hints), CancellationToken.None);
		created.Should().ContainSingle().Which.NormalizedDescriptionId.Should().BeNull();
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		ReceiptItemEntity persisted = await verify.ReceiptItems.SingleAsync(item => item.ReceiptId == seed.Receipt);
		persisted.NormalizedDescriptionId.Should().BeNull();
		persisted.NormalizedDescriptionMatchScore.Should().BeNull();
	}

	[Theory]
	[InlineData("unchanged")]
	[InlineData("newer-edit")]
	[InlineData("manual-link")]
	[InlineData("edit-revert")]
	[InlineData("link-revert")]
	[InlineData("deleted")]
	public async Task TemplateUpdateCannotOverwriteChangesCommittedDuringCanonicalResolution(string change)
	{
		Seed seed = await SeedAsync();
		await using (ApplicationDbContext matching = fixture.CreateDbContext())
		{
			(await matching.NormalizedDescriptions.SingleAsync(row => row.Id == seed.Milk)).CanonicalName = "Requested change";
			await matching.SaveChangesAsync();
		}
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Mock<INormalizedDescriptionService> canonical = new();
		canonical.Setup(service => service.GetOrCreateForTemplateAsync("Requested change", It.IsAny<CancellationToken>())).Returns(async (string _, CancellationToken cancellationToken) =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(cancellationToken);
			return new NormalizedDescription(seed.Milk, "Requested change", NormalizedDescriptionStatus.Active, DateTimeOffset.UtcNow);
		});
		using ServiceProvider provider = BuildProvider(canonical.Object);
		ItemTemplate requested = new(seed.Template, "Requested change", defaultCategory: "Stale category");
		Task updating = provider.GetRequiredService<IItemTemplateService>().UpdateAsync([requested], CancellationToken.None);
		int auditCount;
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await using ApplicationDbContext manual = fixture.CreateDbContext();
			ItemTemplateEntity template = await manual.ItemTemplates.SingleAsync(row => row.Id == seed.Template);
			if (change == "newer-edit") { template.Name = "Newer edit"; template.DefaultCategory = "Fresh category"; }
			else if (change == "manual-link")
			{
				template.NormalizedDescriptionId = seed.Bread;
			}
			else if (change == "edit-revert") { template.Name = "Temporary name"; await manual.SaveChangesAsync(); template.Name = "Original template"; }
			else if (change == "link-revert") { template.NormalizedDescriptionId = seed.Bread; await manual.SaveChangesAsync(); template.NormalizedDescriptionId = seed.Milk; }
			else if (change == "deleted")
			{
				template.DeletedAt = DateTimeOffset.UtcNow;
			}

			await manual.SaveChangesAsync();
			auditCount = await AuditCountAsync(seed.Template);
		}
		finally { release.TrySetResult(); }
		Exception? error = await Record.ExceptionAsync(async () => await updating.WaitAsync(TimeSpan.FromSeconds(10)));
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		ItemTemplateEntity stored = await verify.ItemTemplates.IgnoreQueryFilters().SingleAsync(row => row.Id == seed.Template);
		using AssertionScope assertions = new();
		if (change == "unchanged")
		{
			error.Should().BeNull();
		}
		else
		{
			error.Should().BeOfType<ConcurrencyConflictException>("a requested stale update must surface the intended conflict");
		}

		stored.Name.Should().Be(change == "unchanged" ? "Requested change" : change == "newer-edit" ? "Newer edit" : "Original template");
		stored.DefaultCategory.Should().Be(change == "unchanged" ? "Stale category" : change == "newer-edit" ? "Fresh category" : "Original category");
		stored.NormalizedDescriptionId.Should().Be(change == "manual-link" ? seed.Bread : seed.Milk);
		(await AuditCountAsync(seed.Template)).Should().Be(auditCount + (change == "unchanged" ? 1 : 0), "the rejected stale operation must not write fields or an audit");
	}

	private async Task<Seed> SeedAsync()
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		await context.Database.ExecuteSqlRawAsync("""TRUNCATE "ReceiptItems", "Receipts", "ItemTemplates", "NormalizedDescriptions", "DistinctDescriptions" RESTART IDENTITY CASCADE;""");
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		Guid milk = Guid.NewGuid(), bread = Guid.NewGuid(), template = Guid.NewGuid();
		context.NormalizedDescriptions.AddRange(new() { Id = milk, CanonicalName = "Milk", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow }, new() { Id = bread, CanonicalName = "Bread", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
		context.Receipts.Add(receipt);
		context.ItemTemplates.Add(new() { Id = template, Name = "Original template", DefaultCategory = "Original category", NormalizedDescriptionId = milk });
		await context.SaveChangesAsync();
		return new(receipt.Id, template, milk, bread);
	}

	private static ReceiptItem NewItem(Guid arbitraryCanonical) => new(Guid.NewGuid(), "123", "Entered receipt text", 2, new Money(3, Currency.USD), new Money(6, Currency.USD), "Historical category", null) { NormalizedDescriptionId = arbitraryCanonical, NormalizedDescriptionMatchScore = 0.99 };

	private ServiceProvider BuildProvider(INormalizedDescriptionService canonical, IDbContextFactory<ApplicationDbContext>? factory = null)
	{
		ServiceCollection services = new();
		RegisterServices(services, canonical, factory);
		return services.BuildServiceProvider();
	}

	private void RegisterServices(IServiceCollection services, INormalizedDescriptionService canonical, IDbContextFactory<ApplicationDbContext>? factory = null)
	{
		services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(factory ?? new FixtureFactory(fixture));
		services.AddSingleton(canonical);
		services.AddSingleton<ReceiptItemMapper>().AddSingleton<ReceiptMapper>().AddSingleton<ItemTemplateMapper>();
		services.AddTransient<IReceiptItemRepository, ReceiptItemRepository>().AddTransient<IReceiptRepository, ReceiptRepository>().AddTransient<IItemTemplateRepository, ItemTemplateRepository>();
		services.AddTransient<IReceiptItemService, ReceiptItemService>().AddTransient<IReceiptService, ReceiptService>().AddTransient<IItemTemplateService, ItemTemplateService>();
		services.AddTransient<CreateReceiptItemCommandHandler>();
	}

	private async Task<int> AuditCountAsync(Guid template)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		string id = template.ToString();
		return await context.AuditLogs.CountAsync(audit => audit.EntityType == "ItemTemplate" && audit.EntityId == id);
	}

	private sealed record Seed(Guid Receipt, Guid Template, Guid Milk, Guid Bread);
	private sealed class FixtureFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
