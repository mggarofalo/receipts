using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using FluentAssertions.Execution;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public partial class NormalizedDescriptionStaleWriteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Theory]
	[InlineData("unchanged")]
	[InlineData("description-edit")]
	[InlineData("manual-link")]
	[InlineData("soft-delete")]
	[InlineData("candidate-rejected")]
	[InlineData("candidate-merged")]
	[InlineData("candidate-renamed")]
	[InlineData("raw-rejected")]
	[InlineData("edit-revert")]
	[InlineData("link-unlink")]
	public async Task ResolutionRechecksCurrentOwnership_AfterExpensiveMatching(string change)
	{
		Seed seed = await SeedAsync();
		if (change == "raw-rejected")
		{
			await using ApplicationDbContext before = fixture.CreateDbContext();
			before.NormalizedDescriptions.Remove(await before.NormalizedDescriptions.SingleAsync(row => row.Id == seed.Milk));
			await before.SaveChangesAsync();
		}
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Mock<INormalizedDescriptionService> canonical = CanonicalMock();
		canonical.Setup(service => service.GetOrCreateAsync("Milk", It.IsAny<CancellationToken>())).Returns(async (string _, CancellationToken cancellationToken) =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(cancellationToken);
			return new GetOrCreateResult(new(change == "raw-rejected" ? seed.Bread : seed.Milk, change == "raw-rejected" ? "Bread" : "Milk", NormalizedDescriptionStatus.Active, DateTimeOffset.UtcNow), 0.95);
		});
		using ServiceProvider provider = BuildProvider(canonical.Object);
		using NormalizedDescriptionResolutionService resolver = CreateResolver(provider);
		Task<NormalizedDescriptionResolutionService.ResolutionSummary> resolving = resolver.ProcessPendingResolutionsAsync(CancellationToken.None);
		int auditCount;
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await ApplyChangeAsync(seed, change);
			auditCount = await AuditCountAsync(seed.Item);
		}
		finally
		{
			release.TrySetResult();
		}
		var summary = await resolving.WaitAsync(TimeSpan.FromSeconds(10));
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		ReceiptItemEntity stored = await verify.ReceiptItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == seed.Item);
		using AssertionScope assertions = new();
		bool unchanged = change == "unchanged";
		summary.Linked.Should().Be(unchanged ? 1 : 0);
		summary.Skipped.Should().Be(unchanged ? 0 : 1, "stale work must be observably skipped rather than reported as linked");
		stored.Description.Should().Be(change == "description-edit" ? "Bread" : "Milk");
		stored.NormalizedDescriptionId.Should().Be(unchanged ? seed.Milk : change == "manual-link" ? seed.Bread : null);
		stored.NormalizedDescriptionMatchScore.Should().Be(unchanged ? 0.95 : null);
		(stored.DeletedAt is not null).Should().Be(change == "soft-delete");
		(await AuditCountAsync(seed.Item)).Should().Be(auditCount + (unchanged ? 1 : 0), "a skipped stale result must not create a fictitious business/audit update");
	}

	[Fact]
	public async Task TwoReplicasResolvingTheSameSnapshot_OnlyOneCanCommitItsResult()
	{
		Seed seed = await SeedAsync();
		TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource firstRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource secondRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int arrivals = 0;
		Mock<INormalizedDescriptionService> canonical = CanonicalMock();
		canonical.Setup(service => service.GetOrCreateAsync("Milk", It.IsAny<CancellationToken>())).Returns(async (string _, CancellationToken cancellationToken) =>
		{
			int index = Interlocked.Increment(ref arrivals);
			if (index == 1)
			{
				firstEntered.TrySetResult();
			}

			if (index == 2)
			{
				bothEntered.TrySetResult();
			}

			await (index == 1 ? firstRelease : secondRelease).Task.WaitAsync(cancellationToken);
			return new GetOrCreateResult(new(seed.Milk, "Milk", NormalizedDescriptionStatus.Active, DateTimeOffset.UtcNow), index == 1 ? 0.91 : 0.97);
		});
		using ServiceProvider provider = BuildProvider(canonical.Object);
		using NormalizedDescriptionResolutionService first = CreateResolver(provider), second = CreateResolver(provider);
		Task<NormalizedDescriptionResolutionService.ResolutionSummary> firstRun = first.ProcessPendingResolutionsAsync(CancellationToken.None);
		// Reach the first boundary before starting the second to make the score winner deterministic.
		await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		Task<NormalizedDescriptionResolutionService.ResolutionSummary> secondRun = second.ProcessPendingResolutionsAsync(CancellationToken.None);
		try
		{
			await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			firstRelease.TrySetResult();
			(await firstRun.WaitAsync(TimeSpan.FromSeconds(10))).Linked.Should().Be(1);
		}
		finally
		{
			firstRelease.TrySetResult();
			secondRelease.TrySetResult();
		}
		var loser = await secondRun.WaitAsync(TimeSpan.FromSeconds(10));
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		ReceiptItemEntity stored = await verify.ReceiptItems.AsNoTracking().SingleAsync(item => item.Id == seed.Item);
		using AssertionScope assertions = new();
		loser.Linked.Should().Be(0);
		loser.Skipped.Should().Be(1);
		stored.NormalizedDescriptionMatchScore.Should().Be(0.91, "the second stale snapshot cannot replace the first committed score");
		(await AuditCountAsync(seed.Item)).Should().Be(2, "one insert plus one winning normalization update");
	}

	[Fact]
	public async Task MixedBatch_SkipsOnlyChangedItem_AndNextCycleResolvesItsCurrentText()
	{
		Seed seed = await SeedAsync();
		Guid unchanged;
		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			ReceiptItemEntity original = await setup.ReceiptItems.SingleAsync(item => item.Id == seed.Item);
			ReceiptItemEntity sibling = ReceiptItemEntityGenerator.Generate(original.ReceiptId);
			sibling.Description = "Milk";
			sibling.NormalizedDescriptionId = null;
			sibling.NormalizedDescriptionMatchScore = null;
			unchanged = sibling.Id;
			setup.ReceiptItems.Add(sibling);
			await setup.SaveChangesAsync();
		}
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Mock<INormalizedDescriptionService> canonical = CanonicalMock();
		canonical.Setup(service => service.GetOrCreateAsync("Milk", It.IsAny<CancellationToken>())).Returns(async (string _, CancellationToken token) =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(token);
			return new GetOrCreateResult(new(seed.Milk, "Milk", NormalizedDescriptionStatus.Active, DateTimeOffset.UtcNow), 0.95);
		});
		canonical.Setup(service => service.GetOrCreateAsync("Bread", It.IsAny<CancellationToken>())).ReturnsAsync(new GetOrCreateResult(new(seed.Bread, "Bread", NormalizedDescriptionStatus.Active, DateTimeOffset.UtcNow), 0.96));
		using ServiceProvider provider = BuildProvider(canonical.Object);
		using NormalizedDescriptionResolutionService resolver = CreateResolver(provider);
		var run = resolver.ProcessPendingResolutionsAsync(CancellationToken.None);
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await ApplyChangeAsync(seed, "description-edit");
		}
		finally { release.TrySetResult(); }
		var first = await run.WaitAsync(TimeSpan.FromSeconds(10));
		first.Linked.Should().Be(1);
		first.Skipped.Should().Be(1);
		(await AuditCountAsync(seed.Item)).Should().Be(2, "only its creation and current user edit are committed");
		(await AuditCountAsync(unchanged)).Should().Be(2, "the unchanged sibling has one audited normalization");
		var retry = await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);
		retry.Linked.Should().Be(1);
		retry.Skipped.Should().Be(0);
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		ReceiptItemEntity recovered = await verify.ReceiptItems.SingleAsync(item => item.Id == seed.Item);
		recovered.Description.Should().Be("Bread");
		recovered.NormalizedDescriptionId.Should().Be(seed.Bread);
		recovered.NormalizedDescriptionMatchScore.Should().Be(0.96);
		(await AuditCountAsync(seed.Item)).Should().Be(3);
		canonical.Verify(service => service.GetOrCreateAsync("Milk", It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task DefaultPostgresSearchPath_ResolvesAndRejectsUsingQualifiedTables()
	{
		Seed seed = await SeedAsync();
		Npgsql.NpgsqlConnectionStringBuilder connection = new(fixture.ConnectionString) { SearchPath = "public" };
		Npgsql.NpgsqlDataSourceBuilder builder = new(connection.ConnectionString);
		builder.UseVector();
		await using Npgsql.NpgsqlDataSource dataSource = builder.Build();
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(dataSource, postgres => { postgres.UseVector(); postgres.UsePublicMigrationsHistory(); }).Options;
		Mock<INormalizedDescriptionService> canonical = CanonicalMock();
		canonical.Setup(service => service.GetOrCreateAsync("Milk", It.IsAny<CancellationToken>())).ReturnsAsync(new GetOrCreateResult(new(seed.Milk, "Milk", NormalizedDescriptionStatus.Active, DateTimeOffset.UtcNow), 1));
		using ServiceProvider provider = BuildProvider(canonical.Object, new OptionsFactory(options));
		using NormalizedDescriptionResolutionService resolver = CreateResolver(provider);
		(await resolver.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(1);
		(await CanonicalService(options).UpdateStatusAsync(seed.Milk, NormalizedDescriptionStatus.Rejected, CancellationToken.None)).Should().BeTrue();
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await verify.ReceiptItems.SingleAsync(item => item.Id == seed.Item)).NormalizedDescriptionId.Should().BeNull();
	}

	private async Task ApplyChangeAsync(Seed seed, string change)
	{
		FixtureFactory factory = new(fixture);
		ReceiptItemRepository repository = new(factory);
		if (change is "description-edit" or "edit-revert")
		{
			ReceiptItemEntity item = (await repository.GetByIdAsync(seed.Item, CancellationToken.None))!;
			item.Description = "Bread";
			await repository.UpdateAsync([item], CancellationToken.None);
			if (change == "edit-revert")
			{
				item.Description = "Milk";
				await repository.UpdateAsync([item], CancellationToken.None);
			}
		}
		else if (change is "manual-link" or "link-unlink")
		{
			await using ApplicationDbContext manual = fixture.CreateDbContext();
			ReceiptItemEntity item = await manual.ReceiptItems.SingleAsync(item => item.Id == seed.Item);
			item.NormalizedDescriptionId = seed.Bread;
			item.NormalizedDescriptionMatchScore = null;
			await manual.SaveChangesAsync();
			if (change == "link-unlink")
			{
				item.NormalizedDescriptionId = null;
				await manual.SaveChangesAsync();
			}
		}
		else if (change == "soft-delete")
		{
			await repository.DeleteAsync([seed.Item], CancellationToken.None);
		}
		else if (change == "candidate-merged")
		{
			NormalizedDescriptionService service = new(factory, Embedding(), new NormalizedDescriptionMapper(), new NormalizedDescriptionSettingsMapper());
			await service.MergeAsync(seed.Bread, seed.Milk, CancellationToken.None);
		}
		else if (change == "candidate-renamed")
		{
			await using ApplicationDbContext restore = fixture.CreateDbContext();
			(await restore.NormalizedDescriptions.SingleAsync(row => row.Id == seed.Milk)).CanonicalName = "Restored different text";
			await restore.SaveChangesAsync();
		}
		else if (change is "candidate-rejected" or "raw-rejected")
		{
			if (change == "raw-rejected")
			{
				await using ApplicationDbContext curate = fixture.CreateDbContext();
				curate.NormalizedDescriptions.Add(new() { Id = seed.Milk, CanonicalName = "Milk", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
				await curate.SaveChangesAsync();
			}
			NormalizedDescriptionService service = new(factory, Embedding(), new NormalizedDescriptionMapper(), new NormalizedDescriptionSettingsMapper());
			(await service.UpdateStatusAsync(seed.Milk, NormalizedDescriptionStatus.Rejected, CancellationToken.None)).Should().BeTrue();
		}
	}

	private async Task<Seed> SeedAsync()
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		await context.Database.ExecuteSqlRawAsync("""TRUNCATE "ReceiptItems", "Receipts", "NormalizedDescriptions", "DistinctDescriptions" RESTART IDENTITY CASCADE;""");
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.Description = "Milk";
		item.NormalizedDescriptionId = null;
		item.NormalizedDescriptionMatchScore = null;
		Guid milk = Guid.NewGuid(), bread = Guid.NewGuid();
		context.NormalizedDescriptions.AddRange(CurrentCanonical(milk, "Milk"), CurrentCanonical(bread, "Bread"));
		context.Receipts.Add(receipt);
		context.ReceiptItems.Add(item);
		await context.SaveChangesAsync();
		return new(item.Id, milk, bread);
	}

	private async Task<int> AuditCountAsync(Guid item)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		string id = item.ToString();
		return await context.AuditLogs.CountAsync(audit => audit.EntityType == "ReceiptItem" && audit.EntityId == id);
	}

	private ServiceProvider BuildProvider(INormalizedDescriptionService canonical, IDbContextFactory<ApplicationDbContext>? factory = null)
	{
		ServiceCollection services = new();
		services.AddSingleton(canonical);
		services.AddSingleton(Embedding());
		services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(factory ?? new FixtureFactory(fixture));
		services.AddSingleton<IDescriptionChangeSignal, DescriptionChangeSignal>();
		return services.BuildServiceProvider();
	}

	private static Mock<INormalizedDescriptionService> CanonicalMock()
	{
		Mock<INormalizedDescriptionService> canonical = new();
		canonical
			.Setup(service => service.GetEmbeddingCoverageAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new EmbeddingCoverage(
				OnnxEmbeddingService.EmbeddingSpaceFingerprint,
				CanonicalReady: 0,
				CanonicalTotal: 0,
				ItemReady: 0,
				ItemTotal: 0));
		return canonical;
	}

	private static NormalizedDescriptionEntity CurrentCanonical(Guid id, string name) => new()
	{
		Id = id,
		CanonicalName = name,
		Status = NormalizedDescriptionStatus.Active,
		Embedding = new Pgvector.Vector(new float[OnnxEmbeddingService.EmbeddingDimension]),
		EmbeddingModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
		CreatedAt = DateTimeOffset.UtcNow,
	};

	private static IEmbeddingService Embedding()
	{
		Mock<IEmbeddingService> embedding = new();
		embedding.SetupGet(service => service.IsConfigured).Returns(true);
		return embedding.Object;
	}

	private static NormalizedDescriptionResolutionService CreateResolver(ServiceProvider provider) => new(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IDescriptionChangeSignal>(), NullLogger<NormalizedDescriptionResolutionService>.Instance);
	private sealed record Seed(Guid Item, Guid Milk, Guid Bread);
	private sealed class FixtureFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
