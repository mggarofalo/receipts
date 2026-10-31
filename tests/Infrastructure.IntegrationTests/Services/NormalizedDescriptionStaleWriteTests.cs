using System.Data.Common;
using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using FluentAssertions.Execution;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public partial class NormalizedDescriptionStaleWriteTests(PostgresFixture server) : IsolatedPostgresTest(server), IClassFixture<PostgresFixture>
{
	[Theory]
	[InlineData("unchanged")]
	[InlineData("description-edit")]
	[InlineData("manual-link")]
	[InlineData("soft-delete")]
	[InlineData("candidate-rejected")]
	[InlineData("candidate-merged")]
	[InlineData("candidate-renamed")]
	[InlineData("unrelated-raw-rejected")]
	[InlineData("edit-revert")]
	[InlineData("link-unlink")]
	public async Task ResolutionRechecksCurrentOwnership_AfterCandidateSnapshot(string change)
	{
		Seed seed = await SeedAsync(approximate: true);
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		CandidateReadGate gate = new(async (_, token) =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(token);
		});
		using ServiceProvider provider = BuildProvider(new OptionsFactory(Options(gate)));
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
		bool unchanged = change is "unchanged" or "candidate-renamed" or "candidate-merged" or "unrelated-raw-rejected";
		summary.Linked.Should().Be(unchanged ? 1 : 0);
		summary.Skipped.Should().Be(unchanged ? 0 : 1, "stale work must be observably skipped rather than reported as linked");
		stored.Description.Should().Be(change == "description-edit" ? "Bread" : "Milk");
		stored.NormalizedDescriptionId.Should().Be(change is "candidate-merged" or "manual-link" ? seed.Bread : unchanged || change == "edit-revert" ? seed.Milk : null);
		stored.NormalizedDescriptionMatchScore.Should().BeNull("approved source mappings do not invent similarity scores");
		(stored.DeletedAt is not null).Should().Be(change == "soft-delete");
		(await AuditCountAsync(seed.Item)).Should().Be(auditCount + (unchanged ? 1 : 0), "a skipped stale result must not create a fictitious business/audit update");
	}

	[Fact]
	public async Task CancellationAfterCandidateSnapshot_LeavesNoAssignmentOrAudit_AndCanRetry()
	{
		Seed seed = await SeedAsync();
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		CandidateReadGate gate = new(async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); });
		using ServiceProvider provider = BuildProvider(new OptionsFactory(Options(gate)));
		using var worker = CreateResolver(provider);
		using CancellationTokenSource cancellation = new();
		Task run = worker.ProcessPendingResolutionsAsync(cancellation.Token);
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		cancellation.Cancel();
		Func<Task> cancelled = () => run;
		await cancelled.Should().ThrowAsync<OperationCanceledException>();
		await using (var context = fixture.CreateDbContext())
		{
			(await context.ReceiptItems.SingleAsync(i => i.Id == seed.Item)).NormalizedDescriptionId.Should().BeNull();
		}
		(await AuditCountAsync(seed.Item)).Should().Be(1);
		(await worker.ProcessPendingResolutionsAsync(CancellationToken.None)).Linked.Should().Be(1);
		(await AuditCountAsync(seed.Item)).Should().Be(2);
	}

	[Fact]
	public async Task TwoReplicasResolvingTheSameSnapshot_OnlyOneCanCommitItsResult()
	{
		Seed seed = await SeedAsync(approximate: true);
		TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource firstRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource secondRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int arrivals = 0;
		CandidateReadGate gate = new(async (_, token) =>
		{
			int index = Interlocked.Increment(ref arrivals);
			if (index == 1) { firstEntered.TrySetResult(); }
			if (index == 2) { bothEntered.TrySetResult(); }
			await (index == 1 ? firstRelease : secondRelease).Task.WaitAsync(token);
		}, 2);
		using ServiceProvider provider = BuildProvider(new OptionsFactory(Options(gate)));
		using NormalizedDescriptionResolutionService first = CreateResolver(provider), second = CreateResolver(provider);
		Task<NormalizedDescriptionResolutionService.ResolutionSummary> firstRun = first.ProcessPendingResolutionsAsync(CancellationToken.None);
		// Reach the first snapshot before starting the second to make the winning commit deterministic.
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
		stored.NormalizedDescriptionId.Should().Be(seed.Milk);
		stored.NormalizedDescriptionMatchScore.Should().BeNull();
		(await AuditCountAsync(seed.Item)).Should().Be(2, "one insert plus one winning normalization update");
	}

	[Fact]
	public async Task MixedBatch_SkipsOnlyChangedItem_AndNextCycleResolvesItsCurrentText()
	{
		Seed seed = await SeedAsync(approximate: true);
		Guid unchanged;
		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			await setup.NormalizationSourceMappings.ExecuteUpdateAsync(update => update.SetProperty(mapping => mapping.State, "revoked"));
			ReceiptItemEntity original = await setup.ReceiptItems.SingleAsync(item => item.Id == seed.Item);
			ReceiptItemEntity sibling = ReceiptItemEntityGenerator.Generate(original.ReceiptId);
			sibling.Description = "Milk";
			sibling.ReceiptItemCode = null;
			sibling.NormalizedDescriptionId = null;
			sibling.NormalizedDescriptionMatchScore = null;
			unchanged = sibling.Id;
			setup.ReceiptItems.Add(sibling);
			await setup.SaveChangesAsync();
			await setup.NormalizationSourceMappings.ExecuteUpdateAsync(update => update.SetProperty(mapping => mapping.State, "active"));
		}
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		CandidateReadGate gate = new(async (_, token) =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(token);
		});
		using ServiceProvider provider = BuildProvider(new OptionsFactory(Options(gate)));
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
		await ApproveBreadAsync(seed);
		var retry = await resolver.ProcessPendingResolutionsAsync(CancellationToken.None);
		retry.Linked.Should().Be(1);
		retry.Skipped.Should().Be(0);
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		ReceiptItemEntity recovered = await verify.ReceiptItems.SingleAsync(item => item.Id == seed.Item);
		recovered.Description.Should().Be("Bread");
		recovered.NormalizedDescriptionId.Should().Be(seed.Bread);
		recovered.NormalizedDescriptionMatchScore.Should().BeNull();
		(await AuditCountAsync(seed.Item)).Should().Be(3);
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
		using ServiceProvider provider = BuildProvider(new OptionsFactory(options));
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
				item.ReceiptItemCode = null;
				await repository.UpdateAsync([item], CancellationToken.None);
			}
		}
		else if (change is "manual-link" or "link-unlink")
		{
			// Simulate a concurrent historical assignment writer. SQL revision/evidence triggers
			// must protect the candidate snapshot even when no receipt save policy runs.
			await using ApplicationDbContext manual = fixture.CreateDbContext();
			await manual.Database.ExecuteSqlInterpolatedAsync($"""UPDATE receipts."ReceiptItems" SET "NormalizedDescriptionId" = {seed.Bread}, "NormalizedDescriptionMatchScore" = NULL WHERE "Id" = {seed.Item}""");
			if (change == "link-unlink")
			{
				await manual.Database.ExecuteSqlInterpolatedAsync($"""UPDATE receipts."ReceiptItems" SET "NormalizedDescriptionId" = NULL WHERE "Id" = {seed.Item}""");
			}
		}
		else if (change == "soft-delete")
		{
			await repository.DeleteAsync([seed.Item], CancellationToken.None);
		}
		else if (change == "candidate-merged")
		{
			NormalizedDescriptionService service = new(factory, Embedding(), new NormalizedDescriptionMapper());
			await service.MergeAsync(seed.Bread, seed.Milk, CancellationToken.None);
		}
		else if (change == "candidate-renamed")
		{
			await using ApplicationDbContext restore = fixture.CreateDbContext();
			(await restore.NormalizedDescriptions.SingleAsync(row => row.Id == seed.Milk)).CanonicalName = "Restored different text";
			await restore.SaveChangesAsync();
		}
		else if (change == "unrelated-raw-rejected")
		{
			await using ApplicationDbContext curate = fixture.CreateDbContext();
			curate.NormalizedDescriptions.Add(new() { Id = Guid.NewGuid(), CanonicalName = "Milk", Status = NormalizedDescriptionStatus.Rejected, CreatedAt = DateTimeOffset.UtcNow });
			await curate.SaveChangesAsync();
		}
		else if (change == "candidate-rejected")
		{
			NormalizedDescriptionService service = new(factory, Embedding(), new NormalizedDescriptionMapper());
			(await service.UpdateStatusAsync(seed.Milk, NormalizedDescriptionStatus.Rejected, CancellationToken.None)).Should().BeTrue();
		}
	}

	private async Task<Seed> SeedAsync(bool approximate = false)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.Description = "Milk";
		item.ReceiptItemCode = null;
		item.NormalizedDescriptionId = null;
		item.NormalizedDescriptionMatchScore = null;
		Guid milk = Guid.NewGuid(), bread = Guid.NewGuid();
		context.NormalizedDescriptions.AddRange(CurrentCanonical(milk, approximate ? "Canonical Milk" : "Milk"), CurrentCanonical(bread, approximate ? "Canonical Bread" : "Bread"));
		context.Receipts.Add(receipt);
		context.ReceiptItems.Add(item);
		await context.SaveChangesAsync();
		foreach (var (description, target) in new[] { ("Milk", milk) })
		{
			var key = NormalizationCatalogPolicy.Normalize(new(receipt.Location, description, null));
			context.NormalizationSourceMappings.Add(new() { Id = Guid.NewGuid(), SourceHash = NormalizationCatalogPolicy.Hash(key), Location = key.Location, Description = key.Description, TargetId = target, ApprovedBy = "test-reviewer", ApprovedAt = DateTimeOffset.UtcNow });
		}
		await context.SaveChangesAsync();
		return new(item.Id, milk, bread);
	}

	private async Task ApproveBreadAsync(Seed seed)
	{
		await using var context = fixture.CreateDbContext();
		var item = await context.ReceiptItems.SingleAsync(i => i.Id == seed.Item);
		var receipt = await context.Receipts.SingleAsync(r => r.Id == item.ReceiptId);
		var key = NormalizationCatalogPolicy.Normalize(new(receipt.Location, "Bread", null));
		context.NormalizationSourceMappings.Add(new() { Id = Guid.NewGuid(), SourceHash = NormalizationCatalogPolicy.Hash(key), Location = key.Location, Description = key.Description, TargetId = seed.Bread, ApprovedBy = "reviewer", ApprovedAt = DateTimeOffset.UtcNow });
		await context.SaveChangesAsync();
	}

	private async Task<int> AuditCountAsync(Guid item)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		string id = item.ToString();
		return await context.AuditLogs.CountAsync(audit => audit.EntityType == "ReceiptItem" && audit.EntityId == id);
	}

	private ServiceProvider BuildProvider(IDbContextFactory<ApplicationDbContext>? factory = null)
	{
		ServiceCollection services = new();
		services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(factory ?? new FixtureFactory(fixture));
		services.AddSingleton<IDescriptionChangeSignal, DescriptionChangeSignal>();
		return services.BuildServiceProvider();
	}

	// Pause after PostgreSQL executed the candidate SELECT, before any write locks.
	// The reader retains the original snapshot while independent connections edit current rows.
	private sealed class CandidateReadGate(Func<int, CancellationToken, Task> pause, int maximum = 1) : DbCommandInterceptor
	{
		private int arrivals;
		public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("ReceiptItems", StringComparison.Ordinal) && command.CommandText.Contains("LIMIT", StringComparison.Ordinal))
			{
				int index = Interlocked.Increment(ref arrivals);
				if (index <= maximum) { await pause(index, cancellationToken); }
			}
			return result;
		}
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
