using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Configurations;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

// A dedicated database is necessary: initial migration intentionally rejects any existing taxonomy.
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class LegacyTaxonomyMigrationTests : IClassFixture<PostgresFixture>
{
	private readonly PostgresFixture fixture;
	public LegacyTaxonomyMigrationTests(PostgresFixture fixture) => this.fixture = fixture;
	private sealed class Factory(PostgresFixture fixture, Func<ApplicationDbContext>? create = null) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => create?.Invoke() ?? fixture.CreateDbContext(); }

	private sealed class ObservedContext(DbContextOptions<ApplicationDbContext> options, Func<Task> afterSave) : ApplicationDbContext(options)
	{
		private bool observed;
		public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
		{
			int result = await base.SaveChangesAsync(cancellationToken);
			if (!observed) { observed = true; await afterSave(); }
			return result;
		}
	}

	[Fact]
	public async Task FrozenMigrationRollsBackRetriesFencesUncoordinatedWritersAndPreservesTrash()
	{
		Guid category = Guid.NewGuid(), receipt = Guid.NewGuid(), item = Guid.NewGuid();
		DateTimeOffset boundary = DateTimeOffset.UtcNow;
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			db.Categories.Add(new CategoryEntity { Id = category, Name = "Grocery", IsActive = false });
			db.Receipts.Add(new ReceiptEntity { Id = receipt, Location = "Migration fixture", Date = new(2020, 1, 1) });
			db.ReceiptItems.Add(new ReceiptItemEntity { Id = item, ReceiptId = receipt, Category = "Grocery", Description = "Preserved milk", Quantity = 2, UnitPrice = 3, TotalAmount = 6 });
			await db.SaveChangesAsync();
			// Existing trash must be observed, never resurrected by migration.
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"DeletedAt\" = {boundary} WHERE \"Id\" = {item}");
		}
		LegacyTaxonomyMigrationService service = new(new Factory(fixture));
		LegacyMigrationPlan plan = await service.PreflightAsync(new("frozen-test-dataset", Guid.NewGuid(), "maintenance-admin", boundary));
		plan.Counts.TrashedItems.Should().Be(1);
		Guid archivedCategoryId = plan.Crosswalks.Single(row => row.Source == LegacyTaxonomySource.Category && row.SourceId == category).TargetId;
		plan.Nodes.Single(row => row.Node.Id == archivedCategoryId).Node.Archived.Should().BeTrue();
		LegacyMigrationRequest source = LegacyMigrationFreeze.Source(plan);
		// A frozen request must detect changed database evidence before any history commits.
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{ await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE library.\"Categories\" SET \"Description\" = 'changed' WHERE \"Id\" = {category}"); }
		Func<Task> changedSource = () => service.ApplyAsync(source);
		await changedSource.Should().ThrowAsync<LegacyMigrationReplayException>();
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{ await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE library.\"Categories\" SET \"Description\" = NULL WHERE \"Id\" = {category}"); }
		LegacyTaxonomyMigrationService failing = new(new Factory(fixture, () => new ObservedContext(fixture.CreateOptions(),
			() => throw new InvalidOperationException("injected rollback"))));
		Func<Task> fail = () => failing.ApplyAsync(source);
		await fail.Should().ThrowAsync<InvalidOperationException>().WithMessage("injected rollback");
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			(await db.TaxonomyHeads.SingleAsync()).RevisionId.Should().Be(TaxonomyConfiguration.GenesisId);
			(await db.LegacyMigrations.CountAsync()).Should().Be(0);
			(await db.Classifications.CountAsync()).Should().Be(0);
		}
		(await service.ReadStatusAsync()).Backfilled.Should().BeFalse();

		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		LegacyTaxonomyMigrationService paused = new(new Factory(fixture, () => new ObservedContext(fixture.CreateOptions(), async () =>
		{ entered.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(30)); })));
		Task<LegacyMigrationPlan> apply = paused.ApplyAsync(source);
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
		await using NpgsqlConnection writer = new(fixture.ConnectionString);
		await writer.OpenAsync();
		int pid = writer.ProcessID;
		await using NpgsqlCommand update = new("UPDATE library.\"Categories\" SET \"Description\" = 'after migration' WHERE \"Id\" = @id", writer);
		update.Parameters.AddWithValue("id", category);
		Task<int> competing = update.ExecuteNonQueryAsync();
		try
		{
			await using ApplicationDbContext observer = fixture.CreateDbContext();
			bool blocked = false;
			using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
			while (!blocked)
			{
				blocked = await observer.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {pid} AND wait_event_type = 'Lock') AS \"Value\"").SingleAsync(deadline.Token);
				if (!blocked) { await Task.Delay(25, deadline.Token); }
			}
			competing.IsCompleted.Should().BeFalse();
		}
		finally { release.TrySetResult(); }
		(await apply).Should().BeEquivalentTo(plan);
		(await competing).Should().Be(1);
		(await service.ApplyAsync(source)).Should().BeEquivalentTo(plan);
		Func<Task> changed = () => service.ApplyAsync(source with { ObservedAt = boundary.AddSeconds(1) });
		await changed.Should().ThrowAsync<LegacyMigrationReplayException>();
		(await service.ReadStatusAsync()).Backfilled.Should().BeTrue();
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			var stored = await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().SingleAsync(row => row.Id == item);
			stored.DeletedAt.Should().BeCloseTo(boundary, TimeSpan.FromMilliseconds(1));
			stored.TotalAmount.Should().Be(6);
			stored.Description.Should().Be("Preserved milk");
			(await db.Classifications.SingleAsync()).Source.Should().Be("legacy_import");
			(await db.Classifications.SingleAsync()).CategoryId.Should().Be(archivedCategoryId);
			(await db.Classifications.SingleAsync()).RecordedAt.Should().BeCloseTo(boundary, TimeSpan.FromMilliseconds(1));
			(await db.LegacyMigrationItems.SingleAsync()).Resolution.Should().Be("ResolvedSuggestion");
			(await db.CurrentClassifications.CountAsync()).Should().Be(1);
			(await db.TaxonomyRevisions.CountAsync()).Should().Be(2);
			foreach (string command in new[] { "DELETE FROM taxonomy.\"LegacyMigrations\"", "DELETE FROM taxonomy.\"LegacyCrosswalks\"", "DELETE FROM taxonomy.\"LegacyItemOutcomes\"" })
			{
				Func<Task> mutate = () => db.Database.ExecuteSqlRawAsync(command);
				await mutate.Should().ThrowAsync<PostgresException>().WithMessage("*immutable*");
			}
		}
		Func<Task> unrelated = () => service.PreflightAsync(new("different-dataset", Guid.NewGuid(), "maintenance-admin", boundary));
		await unrelated.Should().ThrowAsync<InvalidOperationException>().WithMessage("*untouched empty genesis*");
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			db.ReceiptItems.Add(new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receipt, Description = "later import" });
			await db.SaveChangesAsync();
		}
		(await service.ReadStatusAsync()).Backfilled.Should().BeFalse();
	}
}

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class LegacyTaxonomyMigrationLimitTests : IClassFixture<PostgresFixture>
{
	private readonly PostgresFixture fixture;
	public LegacyTaxonomyMigrationLimitTests(PostgresFixture fixture) => this.fixture = fixture;
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }

	[Fact]
	public async Task SourceAdmissionCountsInactiveAndTrashedRowsWithoutTruncation()
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO library."Categories" ("Id", "Name", "IsActive", "DeletedAt")
            SELECT gen_random_uuid(), 'limit-' || n, false, CURRENT_TIMESTAMP FROM generate_series(1, 100001) n
            """);
		LegacyTaxonomyMigrationService service = new(new Factory(fixture));
		Func<Task> preflight = () => service.PreflightAsync(new("oversize", Guid.NewGuid(), "maintenance-admin", DateTimeOffset.UtcNow));
		await preflight.Should().ThrowAsync<InvalidOperationException>().WithMessage("*100000*including trash*");
		(await db.TaxonomyHeads.SingleAsync()).RevisionId.Should().Be(TaxonomyConfiguration.GenesisId);
		(await db.LegacyMigrations.CountAsync()).Should().Be(0);
		(await db.Classifications.CountAsync()).Should().Be(0);
	}
}
