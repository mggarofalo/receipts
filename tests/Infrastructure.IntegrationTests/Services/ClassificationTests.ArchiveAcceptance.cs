using System.Text.Json;
using Application.Normalization;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	private Task<string> AcceptanceExport() => new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
	private async Task AcceptanceImport(string path)
	{
		await using var stream = File.OpenRead(path);
		await new BackupImportService(new Factory(fixture), NullLogger<BackupImportService>.Instance).ImportFromSqliteAsync(stream, default);
	}

	private static async Task AcceptanceSql(string path, string sql)
	{
		await using var sqlite = new SqliteConnection($"Data Source={path};Pooling=False");
		await sqlite.OpenAsync();
		await using var command = sqlite.CreateCommand(); command.CommandText = sql;
		await command.ExecuteNonQueryAsync();
	}

	// Complete source, classification, historical and source-restore audit rows.
	// Budget attribution columns are excluded: a downgrade may legitimately roll back the
	// envelope migration (dropping them) before the protected migration's guard refuses.
	private static async Task<string> AcceptanceDatabaseFacts(ApplicationDbContext db)
	{
		return await db.Database.SqlQueryRaw<string>("""
            SELECT coalesce(jsonb_agg(fact ORDER BY fact)::text, '[]') AS "Value" FROM (
              SELECT 'items:' || (to_jsonb(t) - 'EnvelopeId' - 'EnvelopeSource' - 'OccasionId')::text AS fact FROM receipts."ReceiptItems" t
              UNION ALL SELECT 'receipts:' || (to_jsonb(t) - 'PaidFromEnvelopeId' - 'OccasionId')::text FROM receipts."Receipts" t
              UNION ALL SELECT 'assignments:' || to_jsonb(t)::text FROM taxonomy."Assignments" t
              UNION ALL SELECT 'pointers:' || to_jsonb(t)::text FROM taxonomy."CurrentAssignments" t
              UNION ALL SELECT 'operations:' || to_jsonb(t)::text FROM taxonomy."HistoricalOperations" t
              UNION ALL SELECT 'manifest:' || to_jsonb(t)::text FROM taxonomy."HistoricalOperationItems" t
              UNION ALL SELECT 'sourceaudit:' || to_jsonb(t)::text FROM matching."SourceRevisionRestores" t
            ) facts
            """).SingleAsync();
	}

	[Fact]
	public async Task ArchiveAcceptanceV11ActualImportPreservesRetainedHistoricalEvidence()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		await using var before = fixture.CreateDbContext();
		var headers = await before.HistoricalOperations.AsNoTracking().ToListAsync();
		var manifests = await before.HistoricalOperationItems.AsNoTracking().ToListAsync();
		var assignments = await before.Classifications.AsNoTracking().ToListAsync();
		var pointers = await before.CurrentClassifications.AsNoTracking().ToListAsync();
		string path = await AcceptanceExport();
		try
		{
			await AcceptanceSql(path, "UPDATE backup_metadata SET value='11' WHERE key='export_version'; DROP TABLE historical_operation_archive; DROP TABLE source_revision_archive;");
			await AcceptanceImport(path);
			await using var after = fixture.CreateDbContext();
			(await after.HistoricalOperations.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(headers);
			(await after.HistoricalOperationItems.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(manifests);
			(await after.Classifications.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(assignments);
			(await after.CurrentClassifications.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(pointers);
			(await Historical().ReadAsync(Actor, request.OperationId)).Total.Should().Be(1);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task ArchiveAcceptanceSourceMismatchRejectsBeforeAnyDestinationMutation()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		string path = await AcceptanceExport();
		try
		{
			await using var db = fixture.CreateDbContext();
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"Description\" = 'Destination changed after export' WHERE \"Id\" = {seed.Item}");
			string before = await AcceptanceDatabaseFacts(db);
			Func<Task> import = () => AcceptanceImport(path);
			(await import.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("destination source revision");
			(await AcceptanceDatabaseFacts(db)).Should().Be(before);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task ArchiveAcceptanceV12OfflineConsolidationPreservesHistoricalEvidenceAndRestores()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		Guid survivor = Guid.NewGuid(), loser = Guid.NewGuid();
		await using (var db = fixture.CreateDbContext())
		{
			db.NormalizedDescriptions.AddRange(new NormalizedDescriptionEntity { Id = survivor, CanonicalName = "Survivor " + survivor, Status = Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow },
				new NormalizedDescriptionEntity { Id = loser, CanonicalName = "Retired " + loser, Status = Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
			await db.SaveChangesAsync();
		}
		string source = await AcceptanceExport();
		string output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"), manifestPath = output + ".input.json";
		try
		{
			byte[] original = await File.ReadAllBytesAsync(source);
			var inspection = new OfflinePortableBackupInspection();
			var baseline = await inspection.InspectAsync(source);
			baseline.FormatVersion.Should().Be(15);
			baseline.RowCounts["historical_operation_archive"].Should().BeGreaterThan(0);
			var merge = new OfflineConsolidationDecision(Guid.NewGuid(), loser, survivor);
			var name = new OfflineCanonicalDecision(Guid.NewGuid(), survivor, "Survivor " + survivor, null);
			var now = DateTimeOffset.UtcNow.AddSeconds(-1);
			var manifest = new OfflineNormalizationManifest(1, baseline.SourceSha256, baseline.FormatVersion, baseline.FactualBaselineSha256,
				"archive-acceptance", "synthetic-proposer", now.AddMinutes(-1), [name], [], [merge], [],
				new("synthetic-reviewer", now, [new(merge.DecisionId, "Same synthetic product identity"), new(name.DecisionId, "Reviewed synthetic survivor name")]));
			await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, OfflineNormalizationManifestPolicy.JsonOptions));
			var service = new PortableBackupNormalizationService();
			await service.ApplyAsync(source, manifestPath, output);
			await service.VerifyAsync(source, manifestPath, output);
			var transformed = await inspection.InspectAsync(output);
			transformed.FormatVersion.Should().Be(15);
			inspection.VerifyBaseline(baseline, transformed);
			transformed.FactualTableSha256["historical_operation_archive"].Should().Be(baseline.FactualTableSha256["historical_operation_archive"]);
			transformed.FactualTableSha256["source_revision_archive"].Should().Be(baseline.FactualTableSha256["source_revision_archive"]);
			(await File.ReadAllBytesAsync(source)).Should().Equal(original);
			await AcceptanceImport(output);
			await using var restored = fixture.CreateDbContext();
			(await restored.NormalizedDescriptions.AnyAsync(x => x.Id == loser)).Should().BeFalse();
			(await restored.NormalizedDescriptions.AnyAsync(x => x.Id == survivor)).Should().BeTrue();
			(await Historical().ReadAsync(Actor, request.OperationId)).Total.Should().Be(1);
			(await Historical().ApplyChunkAsync(Actor, request.OperationId, 1)).Applied.Should().Be(1);
		}
		finally
		{
			File.Delete(source);
			foreach (string suffix in new[] { "", ".input.json", ".manifest.json", ".lineage.json" }) { File.Delete(output + suffix); }
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ArchiveAcceptanceSourceMigrationDowngradeBoundary(bool retainedAudit)
	{
		var isolated = new PostgresFixture("20261030134404_AddEnvelopes"); await isolated.InitializeAsync();
		try { await new ClassificationTests(isolated).AcceptanceSourceDowngrade(retainedAudit); }
		finally { await isolated.DisposeAsync(); }
	}

	private async Task AcceptanceSourceDowngrade(bool retainedAudit)
	{
		const string migration = "20261026133000_AddPortableSourceRevisionRestore";
		await using var db = fixture.CreateDbContext();
		if (retainedAudit)
		{
			var seed = await HistoricalSeedAsync();
			Guid before = await db.ReceiptItems.Where(x => x.Id == seed.Item).Select(x => x.NormalizationRevision).SingleAsync();
			await using var tx = await db.Database.BeginTransactionAsync();
			await db.Database.ExecuteSqlRawAsync("LOCK TABLE receipts.\"ReceiptItems\" IN EXCLUSIVE MODE");
			await db.Database.ExecuteSqlInterpolatedAsync($"SELECT matching.restore_source_revision('ReceiptItems', {seed.Item}, {before}, {Guid.NewGuid()})");
			await tx.CommitAsync();
		}
		string[] applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
		int index = Array.IndexOf(applied, migration); index.Should().BeGreaterThan(0);
		if (retainedAudit)
		{
			string before = await AcceptanceDatabaseFacts(db);
			before.Should().Contain("sourceaudit:", "the downgrade must encounter retained restore evidence");
			Func<Task> downgrade = () => db.GetService<IMigrator>().MigrateAsync(applied[index - 1]);
			(await downgrade.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("retained source restore evidence");
			(await db.Database.GetAppliedMigrationsAsync()).Should().Equal(applied.Take(index + 1),
					"the protected source restore migration and every prior migration must remain applied");
			(await AcceptanceDatabaseFacts(db)).Should().Be(before);
		}
		else
		{
			await db.GetService<IMigrator>().MigrateAsync(applied[index - 1]);
			(await db.Database.GetAppliedMigrationsAsync()).Should().NotContain(migration);
			await db.GetService<IMigrator>().MigrateAsync(applied[^1]);
			(await db.Database.GetAppliedMigrationsAsync()).Should().Equal(applied);
			(await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM matching.\"SourceRevisionRestores\"").SingleAsync()).Should().Be(0);
		}
	}
}

