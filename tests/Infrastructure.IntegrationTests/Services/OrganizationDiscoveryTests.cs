using Application.Taxonomy;
using Common;
using FluentAssertions;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed partial class OrganizationDiscoveryTests(PostgresFixture server) : IClassFixture<PostgresFixture>
{
	private sealed class Factory(PostgresFixture fixture, IInterceptor? interceptor = null) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => interceptor == null ? fixture.CreateDbContext() :
			new(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions()).AddInterceptors(interceptor).Options);
	}
	private static OrganizationDiscoveryStore Store(PostgresFixture fixture, IInterceptor? interceptor = null) =>
		new(new Factory(fixture, interceptor), TimeProvider.System);
	private static async Task<Guid> Start(PostgresFixture fixture)
	{
		await using var db = fixture.CreateDbContext();
		await Enable(fixture);
		return await Store(fixture).StartAsync((await db.CategoryAssistanceSettings.SingleAsync()).Revision, default);
	}
	private static async Task Enable(PostgresFixture fixture)
	{
		await using var db = fixture.CreateDbContext();
		await db.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"Settings\" SET \"Enabled\" = TRUE, \"Provider\" = 'jev', \"Model\" = 'synthetic-local-only'");
	}
	private static ReceiptItemEntity Item(Guid receipt, Guid id, string description = "Repeated purchase") => new()
	{
		Id = id,
		ReceiptId = receipt,
		Description = description,
		Category = "",
		UnitPriceCurrency = Currency.USD,
		TotalAmountCurrency = Currency.USD
	};
	private static async Task<Guid> Seed(PostgresFixture fixture, params ReceiptItemEntity[] items)
	{
		Guid receipt = items[0].ReceiptId;
		await using var db = fixture.CreateDbContext();
		db.Receipts.Add(new ReceiptEntity { Id = receipt, Location = "Test store", Date = new(2020, 1, 1), TaxAmountCurrency = Currency.USD });
		db.ReceiptItems.AddRange(items);
		await db.SaveChangesAsync();
		return receipt;
	}
	private static async Task Drain(PostgresFixture fixture, Guid run, int chunk = 31)
	{
		for (int n = 0; n < 40; n++)
		{
			if (await Store(fixture).DiscoverAsync(run, chunk, default) == 0)
			{
				return;
			}
		}

		throw new InvalidOperationException("Discovery did not quiesce in bounded passes");
	}

	[Fact]
	public async Task ChunkedGuidSweepRestartAndCompetingScannersCaptureEveryPurchaseWithoutEffectsOrAuditCopies()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			Guid receipt = Guid.NewGuid();
			var items = Enumerable.Range(0, 137).Select(_ => Item(receipt, Guid.NewGuid())).ToArray();
			await Seed(fixture, items);
			await using var db = fixture.CreateDbContext();
			// Remove inbox assistance so every GUID must pass the translated keyset predicate.
			await db.OrganizationDirtyItems.ExecuteDeleteAsync();
			long audits = await db.AuditLogs.LongCountAsync();
			var before = await db.ReceiptItems.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
			Guid run = await Start(fixture);
			(await Start(fixture)).Should().Be(run);
			await Task.WhenAll(Store(fixture).DiscoverAsync(run, 31, default), Store(fixture).DiscoverAsync(run, 31, default));
			await Drain(fixture, run);
			(await db.OrganizationItems.Where(x => x.RunId == run).Select(x => x.ItemId).ToListAsync()).Should().BeEquivalentTo(items.Select(x => x.Id));
			(await db.OrganizationSources.CountAsync(x => x.RunId == run)).Should().Be(1);
			(await db.OrganizationRuns.SingleAsync(x => x.Id == run)).State.Should().Be("watching");
			(await db.OrganizationItems.Select(x => x.State).Distinct().ToListAsync()).Should().Equal("observed");
			(await db.ReceiptItems.AsNoTracking().OrderBy(x => x.Id).ToListAsync()).Should().BeEquivalentTo(before);
			(await db.Classifications.CountAsync()).Should().Be(0);
			(await db.CategoryAssistanceAttempts.CountAsync()).Should().Be(0);
			(await db.AuditLogs.LongCountAsync()).Should().Be(audits);
		}
		finally { await fixture.DisposeAsync(); }
	}

	[Fact]
	public async Task LateCommitAndSourceEditBehindCursorAreRecoveredAndRawWhitespaceSourcesStayDistinct()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			Guid receipt = Guid.NewGuid(), high = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), low = Guid.Parse("00000000-0000-0000-0000-000000000001");
			await Seed(fixture, Item(receipt, high));
			await using var producer = fixture.CreateDbContext();
			await using var tx = await producer.Database.BeginTransactionAsync();
			producer.ReceiptItems.Add(Item(receipt, low));
			await producer.SaveChangesAsync(); // Uncommitted before the high-water snapshot.
			Guid run = await Start(fixture);
			// Scanner may wait on the producer's normalization gate. Let the producer
			// commit while capture is pending, then require the restarted drain to see it.
			Task pendingCapture = Store(fixture).DiscoverAsync(run, 31, default);
			await tx.CommitAsync();
			await pendingCapture;
			await Drain(fixture, run);
			await using var db = fixture.CreateDbContext();
			var old = await db.OrganizationItems.AsNoTracking().SingleAsync(x => x.ItemId == low);
			(await db.OrganizationItems.CountAsync(x => x.RunId == run)).Should().Be(2);
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"Description\" = ' Repeated purchase ' WHERE \"Id\" = {low}");
			await Drain(fixture, run);
			var changed = await db.OrganizationItems.AsNoTracking().SingleAsync(x => x.ItemId == low);
			changed.ItemRevision.Should().NotBe(old.ItemRevision);
			changed.SourceId.Should().NotBeNull();
			changed.SourceId!.Value.Should().NotBe(old.SourceId!.Value);
			var sources = await db.OrganizationSources.Where(x => x.RunId == run).ToListAsync();
			sources.Should().HaveCount(2);
			sources.Select(x => x.RawHash).Distinct().Should().HaveCount(2);
			sources.Select(x => x.NormalizedHash).Distinct().Should().ContainSingle();
			(await db.OrganizationDirtyItems.CountAsync()).Should().Be(0);
		}
		finally { await fixture.DisposeAsync(); }
	}

	private sealed class FailSave : SaveChangesInterceptor
	{
		public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("synthetic interruption before discovery commit");
	}
	[Fact]
	public async Task FailedCaptureRollsBackCursorManifestAndInboxAckThenRestartRecovers()
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			var item = Item(Guid.NewGuid(), Guid.NewGuid()); await Seed(fixture, item);
			Guid run = await Start(fixture);
			Func<Task> fail = () => Store(fixture, new FailSave()).DiscoverAsync(run, 10, default);
			await fail.Should().ThrowAsync<InvalidOperationException>().WithMessage("synthetic interruption*");
			await using var db = fixture.CreateDbContext();
			(await db.OrganizationRuns.AsNoTracking().SingleAsync()).Cursor.Should().BeNull();
			(await db.OrganizationItems.CountAsync()).Should().Be(0);
			(await db.OrganizationSources.CountAsync()).Should().Be(0);
			(await db.OrganizationDirtyItems.CountAsync()).Should().Be(1);
			await Drain(fixture, run);
			(await db.OrganizationItems.CountAsync()).Should().Be(1);
			(await db.OrganizationDirtyItems.CountAsync()).Should().Be(0);
		}
		finally { await fixture.DisposeAsync(); }
	}

	[Theory]
	[InlineData("Revision")]
	[InlineData("RestoreGeneration")]
	public async Task ChangedSettingsOrRestoreGenerationStalesRunWithoutAcknowledgingWork(string field)
	{
		var fixture = await server.CreateIsolatedAsync();
		try
		{
			await Seed(fixture, Item(Guid.NewGuid(), Guid.NewGuid())); Guid run = await Start(fixture);
			await using var db = fixture.CreateDbContext();
			if (field == "Revision")
			{
				await db.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"Settings\" SET \"Revision\" = gen_random_uuid()");
			}
			else
			{
				await db.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"Settings\" SET \"RestoreGeneration\" = gen_random_uuid()");
			}

			(await Store(fixture).DiscoverAsync(run, 10, default)).Should().Be(0);
			var stale = await db.OrganizationRuns.AsNoTracking().SingleAsync();
			stale.Active.Should().BeFalse(); stale.State.Should().Be("stale");
			(await db.OrganizationItems.CountAsync()).Should().Be(0);
			(await db.OrganizationDirtyItems.CountAsync()).Should().Be(1);
			Guid next = await Start(fixture); next.Should().NotBe(run);
			await Drain(fixture, next);
			(await db.OrganizationItems.CountAsync(x => x.RunId == next)).Should().Be(1);
		}
		finally { await fixture.DisposeAsync(); }
	}

	[Fact]
	public async Task MigrationDownAndUpRemoveAndReinstallAllInboxTriggersWithoutChangingReceiptFacts()
	{
		var fixture = await server.CreateIsolatedAsync("20261029014900_AddOrganizationDiscovery");
		try
		{
			await using var db = fixture.CreateDbContext();
			db.Database.HasPendingModelChanges().Should().BeFalse();
			string[] applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
			applied[^1].Should().Be("20261029014900_AddOrganizationDiscovery");
			var migrator = db.GetService<IMigrator>();
			await migrator.MigrateAsync(applied[^2]);
			(await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE indexname IN ('IX_Organization_ItemRawSource','IX_Organization_ItemNormalizedSource')").SingleAsync()).Should().Be(0);
			(await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_trigger WHERE tgname LIKE 'organization_%'").SingleAsync()).Should().Be(0);
			await migrator.MigrateAsync(applied[^1]);
			(await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE indexname IN ('IX_Organization_ItemRawSource','IX_Organization_ItemNormalizedSource')").SingleAsync()).Should().Be(2);
			(await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_trigger WHERE tgname LIKE 'organization_%'").SingleAsync()).Should().Be(5);
			// Run current discovery against the current schema.
			await migrator.MigrateAsync();
			var item = Item(Guid.NewGuid(), Guid.NewGuid()); await Seed(fixture, item);
			(await db.OrganizationDirtyItems.Select(x => x.ItemId).SingleAsync()).Should().Be(item.Id);
			Guid run = await Start(fixture);
			await Drain(fixture, run);
			(await db.ReceiptItems.AsNoTracking().SingleAsync()).Description.Should().Be(item.Description);
			var capturedBeforeTax = await db.OrganizationItems.AsNoTracking().SingleAsync();
			var sourceBeforeTax = await db.OrganizationSources.AsNoTracking().SingleAsync();
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"Receipts\" SET \"TaxAmount\" = 1 WHERE \"Id\" = {item.ReceiptId}");
			// Every receipt UPDATE rotates the established normalization fence,
			// including financial edits; capture must refresh that revision.
			(await db.OrganizationDirtyItems.Select(x => x.ItemId).SingleAsync()).Should().Be(item.Id);
			var receiptAfterTax = await db.Receipts.AsNoTracking().SingleAsync();
			receiptAfterTax.NormalizationRevision.Should().NotBe(capturedBeforeTax.ReceiptRevision);
			var factsAfterTax = await db.ReceiptItems.AsNoTracking().SingleAsync();
			await Drain(fixture, run);
			var capturedAfterTax = await db.OrganizationItems.AsNoTracking().SingleAsync();
			capturedAfterTax.ReceiptRevision.Should().Be(receiptAfterTax.NormalizationRevision);
			capturedAfterTax.SourceId.Should().Be(capturedBeforeTax.SourceId);
			capturedAfterTax.ItemRevision.Should().Be(capturedBeforeTax.ItemRevision);
			capturedAfterTax.ExpectedAssignmentId.Should().Be(capturedBeforeTax.ExpectedAssignmentId);
			(await db.OrganizationSources.AsNoTracking().SingleAsync()).Should().BeEquivalentTo(sourceBeforeTax);
			(await db.ReceiptItems.AsNoTracking().SingleAsync()).Should().BeEquivalentTo(factsAfterTax);
			(await db.Receipts.AsNoTracking().SingleAsync()).Should().BeEquivalentTo(receiptAfterTax);
			(await db.Classifications.CountAsync()).Should().Be(0);
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"Receipts\" SET \"Location\" = 'Changed store' WHERE \"Id\" = {item.ReceiptId}");
			(await db.OrganizationDirtyItems.Select(x => x.ItemId).SingleAsync()).Should().Be(item.Id);
			await Drain(fixture, run);
			(await db.OrganizationSources.AsNoTracking().SingleAsync(x => x.Store == "Changed store")).Id.Should().NotBe(sourceBeforeTax.Id);
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"Receipts\" SET \"DeletedAt\" = now() WHERE \"Id\" = {item.ReceiptId}");
			(await db.OrganizationDirtyItems.Select(x => x.ItemId).SingleAsync()).Should().Be(item.Id);
			await Drain(fixture, run);
			(await db.OrganizationItems.AsNoTracking().SingleAsync()).State.Should().Be("unavailable");
		}
		finally { await fixture.DisposeAsync(); }
	}
}
