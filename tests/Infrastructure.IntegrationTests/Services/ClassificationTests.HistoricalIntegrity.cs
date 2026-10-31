using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task HistoricalIntegrityRejectsEveryFrozenFieldAndDeletionButAllowsApplyAndUndo()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		var header = await db.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == request.OperationId);
		var item = await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == request.OperationId);
		Dictionary<string, string> headerChanges = new()
		{
			["Id"] = "gen_random_uuid()",
			["Actor"] = "'CLASSIFICATION-TEST-ADMIN'",
			["RequestHash"] = "'rewritten'",
			["SelectionJson"] = "'[]'::jsonb",
			["TaxonomyRevisionId"] = "gen_random_uuid()",
			["CapturedAt"] = "\"CapturedAt\" + interval '1 second'",
			["Reason"] = "'rewritten'"
		};
		foreach (var (field, value) in headerChanges)
		{
			await RejectHistoricalSql(db, $"UPDATE taxonomy.\"HistoricalOperations\" SET \"{field}\" = {value} WHERE \"Id\" = {{0}}", request.OperationId, "intent is immutable");
		}
		Dictionary<string, string> itemChanges = new()
		{
			["OperationId"] = "gen_random_uuid()",
			["ItemId"] = "gen_random_uuid()",
			["ReceiptId"] = "gen_random_uuid()",
			["Store"] = "\"Store\" || ' '",
			["RawDescription"] = "upper(\"RawDescription\")",
			["ItemCode"] = "CASE WHEN \"ItemCode\" IS NULL THEN '' ELSE NULL END",
			["ItemRevision"] = "gen_random_uuid()",
			["ReceiptRevision"] = "gen_random_uuid()",
			["ExpectedAssignmentId"] = "NULL",
			["PreviousCategoryId"] = "CASE WHEN \"PreviousCategoryId\" IS NULL THEN gen_random_uuid() ELSE NULL END",
			["TargetCategoryId"] = "NULL",
			["ApplyOperationId"] = "gen_random_uuid()",
			["UndoOperationId"] = "gen_random_uuid()"
		};
		foreach (var (field, value) in itemChanges)
		{
			await RejectHistoricalSql(db, $"UPDATE taxonomy.\"HistoricalOperationItems\" SET \"{field}\" = {value} WHERE \"OperationId\" = {{0}}", request.OperationId, "evidence is immutable");
		}
		await RejectHistoricalSql(db, "DELETE FROM taxonomy.\"HistoricalOperationItems\" WHERE \"OperationId\" = {0}", request.OperationId, "evidence is retained");
		await RejectHistoricalSql(db, "DELETE FROM taxonomy.\"HistoricalOperations\" WHERE \"Id\" = {0}", request.OperationId, "evidence is retained");
		await RejectHistoricalSql(db, "TRUNCATE taxonomy.\"HistoricalOperationItems\" CASCADE", request.OperationId, "evidence is retained");
		await RejectHistoricalSql(db, "TRUNCATE taxonomy.\"HistoricalOperations\" CASCADE", request.OperationId, "evidence is retained");
		(await db.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == request.OperationId)).Should().BeEquivalentTo(header);
		(await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == request.OperationId)).Should().BeEquivalentTo(item);
		// Exact no-op updates remain legal, including nullable evidence.
		await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE taxonomy.\"HistoricalOperationItems\" SET \"ItemCode\" = \"ItemCode\" WHERE \"OperationId\" = {request.OperationId}");
		(await Historical().ApplyChunkAsync(Actor, request.OperationId, 1)).Applied.Should().Be(1);
		(await Historical().UndoChunkAsync(Actor, request.OperationId, new(seed.Revision, "Restore frozen evidence"), 1)).Undone.Should().Be(1);
	}

	[Fact]
	public async Task HistoricalIntegrityFreezesFirstPairedUndoIntentAndRejectsInvalidTransitions()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, null);
		await Historical().SavePreviewAsync(request);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		await RejectHistoricalSql(db, "UPDATE taxonomy.\"HistoricalOperations\" SET \"State\" = 'Completed' WHERE \"Id\" = {0}", request.OperationId, "state transition");
		await RejectHistoricalSql(db, "UPDATE taxonomy.\"HistoricalOperations\" SET \"UndoReason\" = 'reason only' WHERE \"Id\" = {0}", request.OperationId, "paired undo intent");
		await RejectHistoricalSql(db, "UPDATE taxonomy.\"HistoricalOperations\" SET \"UndoTaxonomyRevisionId\" = \"TaxonomyRevisionId\" WHERE \"Id\" = {0}", request.OperationId, "paired undo intent");
		await RejectHistoricalSql(db, "UPDATE taxonomy.\"HistoricalOperations\" SET \"UndoTaxonomyRevisionId\" = \"TaxonomyRevisionId\", \"UndoReason\" = E' \t\n' WHERE \"Id\" = {0}", request.OperationId, "paired undo intent");
		await RejectHistoricalSql(db, "UPDATE taxonomy.\"HistoricalOperations\" SET \"UndoTaxonomyRevisionId\" = \"TaxonomyRevisionId\", \"UndoReason\" = U&'\\00A0\\2003' WHERE \"Id\" = {0}", request.OperationId, "paired undo intent");
		await Historical().CancelAsync(Actor, request.OperationId);
		await RejectHistoricalSql(db, "UPDATE taxonomy.\"HistoricalOperations\" SET \"State\" = 'Undoing' WHERE \"Id\" = {0}", request.OperationId, "paired undo intent");
		await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE taxonomy.\"HistoricalOperations\" SET \"UndoTaxonomyRevisionId\" = {seed.Revision}, \"UndoReason\" = 'Frozen' WHERE \"Id\" = {request.OperationId}");
		foreach (string change in new[]
		{
			"\"UndoTaxonomyRevisionId\" = NULL", "\"UndoReason\" = NULL",
			"\"UndoTaxonomyRevisionId\" = NULL, \"UndoReason\" = NULL",
			"\"UndoTaxonomyRevisionId\" = gen_random_uuid()", "\"UndoReason\" = 'frozen'"
		})
		{
			await RejectHistoricalSql(db, $"UPDATE taxonomy.\"HistoricalOperations\" SET {change} WHERE \"Id\" = {{0}}", request.OperationId, "undo intent is immutable");
		}
		(await Historical().UndoChunkAsync(Actor, request.OperationId, new(seed.Revision, "Frozen"), 1)).State.Should().Be(HistoricalOperationState.Undone);
		await RejectHistoricalSql(db, "UPDATE taxonomy.\"HistoricalOperations\" SET \"State\" = 'Preview' WHERE \"Id\" = {0}", request.OperationId, "state transition");
		await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE taxonomy.\"HistoricalOperations\" SET \"State\" = 'Undone', \"UndoReason\" = 'Frozen' WHERE \"Id\" = {request.OperationId}");
		// Portable restoration may insert complete frozen evidence directly. It must
		// reject incoherent imports and preserve the imported intent thereafter.
		const string import = """
            INSERT INTO taxonomy."HistoricalOperations"
            ("Id", "Actor", "RequestHash", "SelectionJson", "TaxonomyRevisionId", "CapturedAt", "Reason",
             "State", "UndoTaxonomyRevisionId", "UndoReason")
            SELECT gen_random_uuid(), "Actor", "RequestHash", "SelectionJson", "TaxonomyRevisionId", "CapturedAt", "Reason",
            """;
		foreach (string intent in new[]
		{
			"'Undone', NULL, NULL", "'Undone', \"TaxonomyRevisionId\", NULL",
			"'Undone', NULL, 'Frozen'", "'Undone', \"TaxonomyRevisionId\", ' '"
		})
		{
			await RejectHistoricalSql(db, import + " " + intent + " FROM taxonomy.\"HistoricalOperations\" WHERE \"Id\" = {0}", request.OperationId, "paired undo intent");
		}
		await db.Database.ExecuteSqlRawAsync(import + " 'Undone', \"TaxonomyRevisionId\", 'Imported frozen intent' FROM taxonomy.\"HistoricalOperations\" WHERE \"Id\" = {0}", request.OperationId);
		Guid imported = await db.HistoricalOperations.Where(x => x.UndoReason == "Imported frozen intent").Select(x => x.Id).SingleAsync();
		await RejectHistoricalSql(db, "UPDATE taxonomy.\"HistoricalOperations\" SET \"UndoReason\" = 'Changed import' WHERE \"Id\" = {0}", imported, "undo intent is immutable");
	}

	[Fact]
	public async Task HistoricalIntegrityEmptyManifestCanCompleteAndUndoAndCancelledPreviewCanUndo()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		request = request with { Groups = [new(new("No such store", Guid.NewGuid().ToString(), null), seed.Root)] };
		(await Historical().SavePreviewAsync(request)).Total.Should().Be(0);
		(await Historical().ApplyChunkAsync(Actor, request.OperationId, 100)).State.Should().Be(HistoricalOperationState.Completed);
		(await Historical().UndoChunkAsync(Actor, request.OperationId, new(seed.Revision, "Empty undo"), 100)).State.Should().Be(HistoricalOperationState.Undone);
		request = request with { OperationId = Guid.NewGuid() };
		await Historical().SavePreviewAsync(request);
		await Historical().CancelAsync(Actor, request.OperationId);
		(await Historical().UndoChunkAsync(Actor, request.OperationId, new(seed.Revision, "Cancelled undo"), 100)).State.Should().Be(HistoricalOperationState.Undone);
	}

	[Fact]
	public async Task HistoricalIntegrityDowngradeRefusesRetainedEvidenceWithoutRemovingSchema()
	{
		// A separate database excludes source-restore audit from other fixture tests:
		// that audit correctly protects its own later migration first.
		var isolated = new PostgresFixture();
		await isolated.InitializeAsync();
		try { await new ClassificationTests(isolated).AssertHistoricalDowngradeBoundary(); }
		finally { await isolated.DisposeAsync(); }
	}

	private async Task AssertHistoricalDowngradeBoundary()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		string[] applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
		int index = Array.IndexOf(applied, "20261026120050_AddHistoricalReclassification");
		index.Should().BeGreaterThan(0);
		Func<Task> downgrade = () => db.GetService<IMigrator>().MigrateAsync(applied[index - 1]);
		(await downgrade.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("historical reclassification evidence is retained");
		// EF safely removes later empty migrations before the protected migration refuses.
		(await db.Database.GetAppliedMigrationsAsync()).Should().Equal(applied.Take(index + 1));
		await db.GetService<IMigrator>().MigrateAsync(applied[^1]);
		(await Historical().ReadAsync(Actor, request.OperationId)).Total.Should().Be(1);
		(await Historical().ApplyChunkAsync(Actor, request.OperationId, 1)).Applied.Should().Be(1);
	}

	[Fact]
	public async Task HistoricalIntegrityEmptySchemaCanDowngradeAndUpgradeAgain()
	{
		const string migration = "20261026120050_AddHistoricalReclassification";
		await using MigrationDatabase database = await MigrationDatabase.CreateAsync(fixture, migration);
		await using ApplicationDbContext db = database.CreateDbContext();
		string[] applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
		int index = Array.IndexOf(applied, migration);
		await db.GetService<IMigrator>().MigrateAsync(applied[index - 1]);
		(await db.Database.GetAppliedMigrationsAsync()).Should().NotContain(migration);
		await db.GetService<IMigrator>().MigrateAsync(migration);
		(await db.Database.GetAppliedMigrationsAsync()).Should().Contain(migration);
		(await db.HistoricalOperations.CountAsync()).Should().Be(0);
		(await db.HistoricalOperationItems.CountAsync()).Should().Be(0);
	}

	private static async Task RejectHistoricalSql(ApplicationDbContext db, string sql, Guid operationId, string message)
	{
		Func<Task> mutate = () => db.Database.ExecuteSqlRawAsync(sql, operationId);
		var error = (await mutate.Should().ThrowAsync<PostgresException>()).Which;
		error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
		error.MessageText.Should().Contain(message);
	}
}
