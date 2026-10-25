using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class PortableTaxonomyTimestampRestoreTests
{
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }

	[Theory]
	[InlineData("submicrosecond")]
	[InlineData("infinity")]
	[InlineData("maximum-finite")]
	public async Task V10_FrozenTimestampEvidenceRestoresUnchangedAndRejectsConflictingMetadata(string boundary)
	{
		var source = new PostgresFixture();
		await source.InitializeAsync();
		try
		{
			Guid receipt = Guid.NewGuid(), item = Guid.NewGuid();
			DateTimeOffset stored = new DateTimeOffset(2026, 10, 25, 21, 36, 16, TimeSpan.Zero).AddTicks(8_930_800);
			DateTimeOffset observed = stored.AddTicks(1);
			if (boundary == "infinity") { stored = observed = DateTimeOffset.MaxValue; }
			if (boundary == "maximum-finite") { observed = DateTimeOffset.MaxValue.AddTicks(-1); stored = DateTimeOffset.MaxValue.AddTicks(-9); }
			await using (var db = source.CreateDbContext())
			{
				db.Receipts.Add(new ReceiptEntity { Id = receipt, Location = "Original store", Date = new(2020, 1, 1) });
				db.ReceiptItems.Add(new ReceiptItemEntity { Id = item, ReceiptId = receipt, Description = "Milk", Category = "Observed grocery", Quantity = 1, UnitPrice = 3, TotalAmount = 3 });
				await db.SaveChangesAsync();
			}
			var migration = new LegacyTaxonomyMigrationService(new Factory(source));
			var plan = await migration.PreflightAsync(new("submicrosecond-roundtrip", Guid.NewGuid(), "maintenance-admin", observed));
			var frozen = LegacyMigrationFreeze.Source(plan);
			await migration.ApplyAsync(frozen);
			LegacyMigrationEntity evidence;
			string crosswalks, outcomes;
			await using (var db = source.CreateDbContext())
			{
				evidence = await db.LegacyMigrations.AsNoTracking().SingleAsync();
				evidence.ObservedAt.Should().Be(stored);
				JsonSerializer.Deserialize<LegacyMigrationPlan>(evidence.PlanJson)!.ObservedAt.Should().Be(observed);
				crosswalks = JsonSerializer.Serialize(await db.LegacyMigrationCrosswalks.AsNoTracking().OrderBy(r => r.SourceId).ToListAsync());
				outcomes = JsonSerializer.Serialize(await db.LegacyMigrationItems.AsNoTracking().OrderBy(r => r.ReceiptItemId).ToListAsync());
			}
			string path = await new BackupService(new Factory(source), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
			var target = new PostgresFixture();
			await target.InitializeAsync();
			try
			{
				var importer = new BackupImportService(new Factory(target), NullLogger<BackupImportService>.Instance);
				async Task Restore()
				{
					await using var input = File.OpenRead(path);
					await importer.ImportFromSqliteAsync(input, CancellationToken.None);
				}
				await Restore();
				await Restore();
				await using (var db = target.CreateDbContext())
				{
					var restored = await db.LegacyMigrations.AsNoTracking().SingleAsync();
					restored.PlanJson.Should().Be(evidence.PlanJson);
					restored.RequestHash.Should().Be(evidence.RequestHash);
					restored.ObservedAt.Should().Be(stored);
					(await db.TaxonomyRevisions.SingleAsync(r => r.Id == plan.RevisionId)).RecordedAt.Should().Be(stored);
					(await db.Classifications.SingleAsync()).RecordedAt.Should().Be(stored);
					JsonSerializer.Serialize(await db.LegacyMigrationCrosswalks.AsNoTracking().OrderBy(r => r.SourceId).ToListAsync()).Should().Be(crosswalks);
					JsonSerializer.Serialize(await db.LegacyMigrationItems.AsNoTracking().OrderBy(r => r.ReceiptItemId).ToListAsync()).Should().Be(outcomes);
				}
				var restoredMigration = new LegacyTaxonomyMigrationService(new Factory(target));
				JsonSerializer.Serialize(await restoredMigration.ApplyAsync(frozen)).Should().Be(evidence.PlanJson);
				// A distinct frozen instant remains a conflicting request even within the same stored microsecond.
				await FluentActions.Invoking(() => restoredMigration.ApplyAsync(frozen with { ObservedAt = observed.AddTicks(observed == DateTimeOffset.MaxValue ? -1 : 1) }))
					.Should().ThrowAsync<LegacyMigrationReplayException>();
				await using (var sqlite = new SqliteConnection($"Data Source={path};Pooling=False"))
				{
					await sqlite.OpenAsync();
					await using var command = sqlite.CreateCommand();
					command.CommandText = "SELECT payload FROM taxonomy_archive WHERE kind='migration'";
					var payload = JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!;
					// Even a single extra 100ns tick in the relational metadata is not its exact storage projection.
					payload["ObservedAt"] = stored.AddTicks(stored == DateTimeOffset.MaxValue ? -1 : 1);
					command.CommandText = "UPDATE taxonomy_archive SET payload=$payload WHERE kind='migration'; UPDATE receipts SET location='must not commit'";
					command.Parameters.AddWithValue("$payload", payload.ToJsonString());
					await command.ExecuteNonQueryAsync();
				}
				await FluentActions.Invoking(Restore).Should().ThrowAsync<InvalidOperationException>().WithMessage("*Migration plan differs from metadata*");
				await using (var db = target.CreateDbContext())
				{
					(await db.Receipts.SingleAsync(r => r.Id == receipt)).Location.Should().Be("Original store");
					(await db.LegacyMigrations.SingleAsync()).PlanJson.Should().Be(evidence.PlanJson);
				}
			}
			finally { await target.DisposeAsync(); File.Delete(path); }
		}
		finally { await source.DisposeAsync(); }
	}
}
