using System.Data.Common;
using Application.Exceptions;
using Application.Interfaces.Services;
using Domain.Core;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public partial class TemplateNormalizationOwnershipTests
{
	[Fact]
	public async Task BatchUpdate_RejectsEveryRequestedChangeAndAudit_WhenOneTemplateChangesDuringMatching()
	{
		Seed seed = await SeedAsync();
		Guid second = Guid.NewGuid();
		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.ItemTemplates.Add(new() { Id = second, Name = "Second original", DefaultCategory = "Second category", NormalizedDescriptionId = seed.Bread });
			await setup.SaveChangesAsync();
		}
		Guid firstTarget = await AddCanonicalAsync("First requested"), secondTarget = await AddCanonicalAsync("Second requested");
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Mock<INormalizedDescriptionService> canonical = new();
		canonical.Setup(service => service.GetOrCreateForTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(async (string name, CancellationToken token) =>
		{
			if (name == "First requested") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
			return Canonical(name == "First requested" ? firstTarget : secondTarget, name);
		});
		using ServiceProvider provider = BuildProvider(canonical.Object);
		Task update = provider.GetRequiredService<IItemTemplateService>().UpdateAsync([new(seed.Template, "First requested"), new(second, "Second requested")], CancellationToken.None);
		int firstAudits = await AuditCountAsync(seed.Template), secondAudits;
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await using ApplicationDbContext edit = fixture.CreateDbContext();
			(await edit.ItemTemplates.SingleAsync(row => row.Id == second)).DefaultCategory = "Manual category";
			await edit.SaveChangesAsync();
			secondAudits = await AuditCountAsync(second);
		}
		finally { release.TrySetResult(); }
		Func<Task> complete = async () => await update.WaitAsync(TimeSpan.FromSeconds(10));
		await complete.Should().ThrowAsync<ConcurrencyConflictException>();
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await verify.ItemTemplates.SingleAsync(row => row.Id == seed.Template)).Name.Should().Be("Original template");
		ItemTemplateEntity other = await verify.ItemTemplates.SingleAsync(row => row.Id == second);
		other.Name.Should().Be("Second original");
		other.DefaultCategory.Should().Be("Manual category");
		(await AuditCountAsync(seed.Template)).Should().Be(firstAudits);
		(await AuditCountAsync(second)).Should().Be(secondAudits);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CanonicalFailureIsOptional_ButCancellationBeforeSaveIsPreserved(bool cancellation)
	{
		Seed seed = await SeedAsync();
		using CancellationTokenSource cts = new();
		Mock<INormalizedDescriptionService> canonical = new();
		canonical.Setup(service => service.GetOrCreateForTemplateAsync("Requested", It.IsAny<CancellationToken>())).Returns(async (string _, CancellationToken token) =>
		{
			await Task.Yield();
			if (cancellation) { cts.Cancel(); token.ThrowIfCancellationRequested(); }
			throw new InvalidOperationException("Synthetic unavailable registry");
		});
		using ServiceProvider provider = BuildProvider(canonical.Object);
		int audits = await AuditCountAsync(seed.Template);
		Func<Task> update = () => provider.GetRequiredService<IItemTemplateService>().UpdateAsync([new(seed.Template, "Requested", "Requested category")], cts.Token);
		if (cancellation)
		{
			await update.Should().ThrowAsync<OperationCanceledException>();
		}
		else
		{
			await update();
		}

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		ItemTemplateEntity stored = await verify.ItemTemplates.SingleAsync(row => row.Id == seed.Template);
		stored.Name.Should().Be(cancellation ? "Original template" : "Requested");
		stored.NormalizedDescriptionId.Should().Be(cancellation ? seed.Milk : null);
		(await AuditCountAsync(seed.Template)).Should().Be(audits + (cancellation ? 0 : 1));
	}

	[Fact]
	public async Task ExplicitTemplateCreation_CanReinstateRejectedText()
	{
		Seed seed = await SeedAsync();
		NormalizedDescriptionService registry = RealRegistry();
		await registry.UpdateStatusAsync(seed.Milk, NormalizedDescriptionStatus.Rejected, CancellationToken.None);
		using ServiceProvider provider = BuildProvider(registry);
		var created = await provider.GetRequiredService<IItemTemplateService>().CreateAsync([new(Guid.NewGuid(), "Milk")], CancellationToken.None);
		created.Should().ContainSingle().Which.NormalizedDescriptionId.Should().Be(seed.Milk);
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await verify.NormalizedDescriptions.SingleAsync(row => row.Id == seed.Milk)).Status.Should().Be(NormalizedDescriptionStatus.Active);
		(await verify.ItemTemplates.SingleAsync(row => row.Name == "Milk")).NormalizedDescriptionId.Should().Be(seed.Milk);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CandidateRejectedDuringMatching_IsNotAttachedAtTheFinalTemplateWrite(bool creating)
	{
		Seed seed = await SeedAsync();
		Guid target = await AddCanonicalAsync("Declared template");
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Mock<INormalizedDescriptionService> canonical = new();
		canonical.Setup(service => service.GetOrCreateForTemplateAsync("Declared template", It.IsAny<CancellationToken>())).Returns(async (string _, CancellationToken token) =>
		{
			entered.TrySetResult(); await release.Task.WaitAsync(token); return Canonical(target, "Declared template");
		});
		using ServiceProvider provider = BuildProvider(canonical.Object);
		IItemTemplateService service = provider.GetRequiredService<IItemTemplateService>();
		Guid id = creating ? Guid.NewGuid() : seed.Template;
		Task operation = creating ? service.CreateAsync([new(id, "Declared template")], CancellationToken.None) : service.UpdateAsync([new(id, "Declared template")], CancellationToken.None);
		try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await RealRegistry().UpdateStatusAsync(target, NormalizedDescriptionStatus.Rejected, CancellationToken.None); }
		finally { release.TrySetResult(); }
		await operation.WaitAsync(TimeSpan.FromSeconds(10));
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		ItemTemplateEntity stored = await verify.ItemTemplates.SingleAsync(row => row.Id == id);
		stored.Name.Should().Be("Declared template");
		stored.NormalizedDescriptionId.Should().BeNull();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task FinalTemplateCommitFailure_RollsBackFieldsAndAudits_AndAllowsRetry(bool creating)
	{
		Seed seed = await SeedAsync();
		Guid target = await AddCanonicalAsync("Declared target");
		Mock<INormalizedDescriptionService> canonical = new();
		canonical.Setup(service => service.GetOrCreateForTemplateAsync("Declared target", It.IsAny<CancellationToken>())).ReturnsAsync(Canonical(target, "Declared target"));
		using ServiceProvider provider = BuildProvider(canonical.Object, new OptionsFactory(Options(new CommitFailureOnce())));
		IItemTemplateService service = provider.GetRequiredService<IItemTemplateService>();
		Guid id = creating ? Guid.NewGuid() : seed.Template;
		int audits = await AuditCountAsync(id);
		Func<Task> write = creating
			? async () => await service.CreateAsync([new(id, "Declared target")], CancellationToken.None)
			: () => service.UpdateAsync([new(id, "Declared target")], CancellationToken.None);
		await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("Synthetic template commit rejection");
		await using (ApplicationDbContext verify = fixture.CreateDbContext())
		{
			ItemTemplateEntity? row = await verify.ItemTemplates.SingleOrDefaultAsync(row => row.Id == id);
			if (creating)
			{
				row.Should().BeNull();
			}
			else { row!.Name.Should().Be("Original template"); row.NormalizedDescriptionId.Should().Be(seed.Milk); }
		}
		(await AuditCountAsync(id)).Should().Be(audits);
		await write();
		(await AuditCountAsync(id)).Should().Be(audits + 1);
	}

	private sealed class CommitFailureOnce : DbTransactionInterceptor
	{
		private bool _failed;
		public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
		{
			if (!_failed) { _failed = true; throw new InvalidOperationException("Synthetic template commit rejection"); }
			return ValueTask.FromResult(result);
		}
	}

	private async Task<Guid> AddCanonicalAsync(string name)
	{
		Guid id = Guid.NewGuid();
		await using ApplicationDbContext context = fixture.CreateDbContext();
		context.NormalizedDescriptions.Add(new() { Id = id, CanonicalName = name, Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
		await context.SaveChangesAsync();
		return id;
	}
	private static NormalizedDescription Canonical(Guid id, string name) => new(id, name, NormalizedDescriptionStatus.Active, DateTimeOffset.UtcNow);
	private NormalizedDescriptionService RealRegistry() => new(new FixtureFactory(fixture), Mock.Of<IEmbeddingService>(), new NormalizedDescriptionMapper(), new NormalizedDescriptionSettingsMapper());
}
