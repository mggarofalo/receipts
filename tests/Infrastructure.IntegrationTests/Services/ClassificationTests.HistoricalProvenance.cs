using System.Text.Json;
using Application.Normalization;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task HistoricalProvenanceMigrationPreservesManualRowsAndGuardsEvidence()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).HistoricalProvenanceMigrationPreservesManualRowsAndGuardsEvidenceCore();
	}

	private async Task HistoricalProvenanceMigrationPreservesManualRowsAndGuardsEvidenceCore()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		await using var db = fixture.CreateDbContext();
		string[] migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
		await db.GetService<IMigrator>().MigrateAsync(migrations[^2]);
		await db.GetService<IMigrator>().MigrateAsync(migrations[^1]);
		var header = await db.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == request.OperationId);
		var item = await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == request.OperationId);
		header.Kind.Should().Be(HistoricalOperationKind.ManualV1);
		item.ApprovedMappingRevisionId.Should().BeNull();
		await RejectHistoricalSql(db, "UPDATE taxonomy.\"HistoricalOperations\" SET \"Kind\" = 'ApprovedMappingV1' WHERE \"Id\" = {0}", request.OperationId, "kind is immutable");
		await RejectHistoricalSql(db, "UPDATE taxonomy.\"HistoricalOperationItems\" SET \"ApprovedMappingRevisionId\" = gen_random_uuid() WHERE \"OperationId\" = {0}", request.OperationId, "provenance is immutable");
		(await Historical().ApplyChunkAsync(Actor, request.OperationId, 1)).Applied.Should().Be(1);
	}

	[Fact]
	public async Task AutomaticHistoricalEvidenceRequiresMatchingApprovalAndCannotRunOrExport()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).AutomaticHistoricalEvidenceRequiresMatchingApprovalAndCannotRunOrExportCore();
	}

	private async Task AutomaticHistoricalEvidenceRequiresMatchingApprovalAndCannotRunOrExportCore()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		await using var db = fixture.CreateDbContext();
		var manual = await db.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == request.OperationId);
		var item = await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == request.OperationId);
		var source = new NormalizationSourceKey(item.Store, item.RawDescription, item.ItemCode);
		var mapping = await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor,
			new(source, NormalizationCatalogPolicy.Hash(source), null, seed.Revision, seed.Root, Guid.NewGuid(), "Approved"));
		manual.Id = Guid.NewGuid();
		manual.Kind = HistoricalOperationKind.ApprovedMappingV1;
		db.HistoricalOperations.Add(manual);
		await db.SaveChangesAsync();
		item.OperationId = manual.Id;
		item.ApplyOperationId = Guid.NewGuid();
		item.UndoOperationId = Guid.NewGuid();
		db.HistoricalOperationItems.Add(item);
		var invalid = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
		((PostgresException)invalid.InnerException!).SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
		db.ChangeTracker.Clear();
		item.ApprovedMappingRevisionId = mapping.Id;
		db.HistoricalOperationItems.Add(item);
		await db.SaveChangesAsync();
		await RejectHistoricalSql(db, "UPDATE taxonomy.\"HistoricalOperationItems\" SET \"ApprovedMappingRevisionId\" = NULL WHERE \"OperationId\" = {0}", manual.Id, "provenance is immutable");
		var error = await Assert.ThrowsAsync<TaxonomyException>(() => Historical().ApplyChunkAsync(Actor, manual.Id, 1));
		error.Status.Should().Be(409);
		await Assert.ThrowsAsync<InvalidDataException>(() => AcceptanceExport());
	}

	[Fact]
	public async Task HistoricalArchiveDefaultsMissingFieldsAndRejectsAutomaticClaims()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).HistoricalArchiveDefaultsMissingFieldsAndRejectsAutomaticClaimsCore();
	}

	private async Task HistoricalArchiveDefaultsMissingFieldsAndRejectsAutomaticClaimsCore()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		string path = await AcceptanceExport();
		try
		{
			await using (var sqlite = new SqliteConnection($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using var command = sqlite.CreateCommand();
				command.CommandText = "SELECT kind,payload FROM historical_operation_archive ORDER BY kind";
				await using var reader = await command.ExecuteReaderAsync();
				var shapes = new Dictionary<string, string[]>();
				while (await reader.ReadAsync())
				{
					using var json = JsonDocument.Parse(reader.GetString(1));
					shapes.Add(reader.GetString(0), json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
				}
				shapes.Keys.Should().BeEquivalentTo(["operation", "item"]);
				shapes["operation"].Should().BeEquivalentTo(["Id", "Actor", "RequestHash", "SelectionJson", "TaxonomyRevisionId", "CapturedAt", "Reason", "State", "UndoTaxonomyRevisionId", "UndoReason"]);
				shapes["item"].Should().BeEquivalentTo(["OperationId", "ItemId", "ReceiptId", "Store", "RawDescription", "ItemCode", "ItemRevision", "ReceiptRevision", "ExpectedAssignmentId", "PreviousCategoryId", "TargetCategoryId", "ApplyOperationId", "UndoOperationId", "State", "AppliedAssignmentId", "Reason", "UndoState", "UndoAssignmentId", "UndoReason"]);
			}
			await AcceptanceImport(path);
			await AcceptanceSql(path, "UPDATE historical_operation_archive SET payload = json_set(payload, '$.Kind', 1) WHERE kind = 'operation'");
			await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptanceImport(path));
		}
		finally { File.Delete(path); }
	}
	[Fact]
	public async Task HistoricalProvenanceDowngradeWaitsForConcurrentAutomaticInsert()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).HistoricalProvenanceDowngradeWaitsForConcurrentAutomaticInsertCore();
	}

	private async Task HistoricalProvenanceDowngradeWaitsForConcurrentAutomaticInsertCore()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		await using var writer = fixture.CreateDbContext();
		await using var transaction = await writer.Database.BeginTransactionAsync();
		var row = await writer.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == request.OperationId);
		row.Id = Guid.NewGuid();
		row.Kind = HistoricalOperationKind.ApprovedMappingV1;
		writer.HistoricalOperations.Add(row);
		await writer.SaveChangesAsync();
		await using var migrator = fixture.CreateDbContext();
		await migrator.Database.OpenConnectionAsync();
		int pid = ((NpgsqlConnection)migrator.Database.GetDbConnection()).ProcessID;
		string[] applied = (await migrator.Database.GetAppliedMigrationsAsync()).ToArray();
		Task downgrade = migrator.GetService<IMigrator>().MigrateAsync(applied[^2]);
		try
		{
			await using var observer = new NpgsqlConnection(fixture.ConnectionString);
			await observer.OpenAsync();
			await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = @pid AND NOT granted AND mode = 'AccessExclusiveLock')", observer);
			command.Parameters.AddWithValue("pid", pid);
			bool waiting = false;
			for (int i = 0; i < 100 && !waiting && !downgrade.IsCompleted; i++)
			{
				waiting = (bool)(await command.ExecuteScalarAsync())!;
				if (!waiting) { await Task.Delay(50); }
			}
			waiting.Should().BeTrue("the downgrade must lock both tables before checking retained evidence");
		}
		finally { await transaction.CommitAsync(); }
		var exception = await Assert.ThrowsAsync<PostgresException>(() => downgrade);
		exception.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
		exception.MessageText.Should().Contain("Automatic historical provenance is retained");
		(await migrator.Database.GetAppliedMigrationsAsync()).Should().Contain(applied[^1]);
	}

}
