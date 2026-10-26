using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	private sealed class RejectDestinationFactory : IDbContextFactory<ApplicationDbContext>
	{
		public int Calls { get; private set; }
		public ApplicationDbContext CreateDbContext() { Calls++; throw new InvalidOperationException("Destination must not be opened"); }
	}

	[Theory]
	[InlineData("missing")]
	[InlineData("extra")]
	[InlineData("kind")]
	[InlineData("invalid revision")]
	[InlineData("empty revision")]
	[InlineData("invalid id")]
	[InlineData("duplicate")]
	public async Task HistoricalPortableMalformedSourceCoverageFailsBeforeDestinationCreation(string corruption)
	{
		await HistoricalSeedAsync();
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		try
		{
			await using (var sqlite = new SqliteConnection($"Data Source={path};Pooling=False"))
			{
				await sqlite.OpenAsync();
				await using var command = sqlite.CreateCommand();
				command.CommandText = corruption switch
				{
					"missing" => "DELETE FROM source_revision_archive WHERE kind='ReceiptItems'",
					"extra" => $"INSERT INTO source_revision_archive VALUES ('ReceiptItems','{Guid.NewGuid()}','{Guid.NewGuid()}')",
					"kind" => "UPDATE source_revision_archive SET kind='Unknown' WHERE kind='ReceiptItems'",
					"invalid revision" => "UPDATE source_revision_archive SET revision='invalid'",
					"empty revision" => "UPDATE source_revision_archive SET revision='00000000-0000-0000-0000-000000000000'",
					"invalid id" => "UPDATE source_revision_archive SET id='invalid' WHERE rowid=(SELECT rowid FROM source_revision_archive LIMIT 1)",
					_ => "ALTER TABLE source_revision_archive RENAME TO original_source; CREATE TABLE source_revision_archive AS SELECT * FROM original_source; INSERT INTO source_revision_archive SELECT * FROM original_source"
				};
				await command.ExecuteNonQueryAsync();
			}
			var destination = new RejectDestinationFactory();
			var importer = new BackupImportService(destination, NullLogger<BackupImportService>.Instance);
			await using var stream = File.OpenRead(path);
			Func<Task> restore = () => importer.ImportFromSqliteAsync(stream, default);
			(await restore.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().ContainEquivalentOf("source revision");
			destination.Calls.Should().Be(0);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public async Task HistoricalPortablePartialUndoRoundTripPreservesExactEvidenceAndResumesOnce()
	{
		var seed = await HistoricalSeedAsync();
		await using (var db = fixture.CreateDbContext())
		{
			var original = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
			var second = SampleData.Entities.ReceiptItemEntityGenerator.Generate(original.ReceiptId);
			second.Description = original.Description;
			second.ReceiptItemCode = original.ReceiptItemCode;
			await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync([second], default);
		}
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		(await Historical().SavePreviewAsync(request)).Total.Should().Be(2);
		(await Historical().ApplyChunkAsync(Actor, request.OperationId, 100)).Applied.Should().Be(2);
		var undo = new HistoricalUndoCommand(seed.Revision, "Preserve first undo intent exactly");
		var partial = await Historical().UndoChunkAsync(Actor, request.OperationId, undo, 1);
		partial.State.Should().Be(HistoricalOperationState.Undoing);
		partial.Undone.Should().Be(1);
		await using var source = fixture.CreateDbContext();
		var header = await source.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == request.OperationId);
		var rows = await source.HistoricalOperationItems.AsNoTracking().Where(x => x.OperationId == request.OperationId).OrderBy(x => x.ItemId).ToListAsync();
		var ids = rows.Select(x => x.ItemId).ToList();
		var assignments = await source.Classifications.AsNoTracking().Where(x => ids.Contains(x.ReceiptItemId)).OrderBy(x => x.Id).ToListAsync();
		var pointers = await source.CurrentClassifications.AsNoTracking().Where(x => ids.Contains(x.ReceiptItemId)).OrderBy(x => x.ReceiptItemId).ToListAsync();
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		var target = new PostgresFixture(); await target.InitializeAsync();
		try
		{
			var factory = new Factory(target);
			var importer = new BackupImportService(factory, NullLogger<BackupImportService>.Instance);
			async Task Restore() { await using var stream = File.OpenRead(path); await importer.ImportFromSqliteAsync(stream, default); }
			await Restore(); await Restore();
			await using var restored = target.CreateDbContext();
			(await restored.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == request.OperationId)).Should().BeEquivalentTo(header);
			(await restored.HistoricalOperationItems.AsNoTracking().Where(x => x.OperationId == request.OperationId).OrderBy(x => x.ItemId).ToListAsync()).Should().BeEquivalentTo(rows);
			(await restored.Classifications.AsNoTracking().Where(x => ids.Contains(x.ReceiptItemId)).OrderBy(x => x.Id).ToListAsync()).Should().BeEquivalentTo(assignments);
			(await restored.CurrentClassifications.AsNoTracking().Where(x => ids.Contains(x.ReceiptItemId)).OrderBy(x => x.ReceiptItemId).ToListAsync()).Should().BeEquivalentTo(pointers);
			var runner = new HistoricalReclassificationService(factory, TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = true }));
			(await runner.ReadAsync(Actor, request.OperationId)).Should().BeEquivalentTo(partial);
			var complete = await runner.UndoChunkAsync(Actor, request.OperationId, undo, 100);
			complete.State.Should().Be(HistoricalOperationState.Undone); complete.Undone.Should().Be(2);
			(await runner.UndoChunkAsync(Actor, request.OperationId, undo, 100)).Should().BeEquivalentTo(complete);
			(await restored.Classifications.CountAsync(x => ids.Contains(x.ReceiptItemId))).Should().Be(assignments.Count + 1);
		}
		finally { await target.DisposeAsync(); File.Delete(path); }
	}

	[Fact]
	public async Task HistoricalSourceRestoreRejectsUnlockedForgedGrant()
	{
		var seed = await HistoricalSeedAsync();
		await using var db = fixture.CreateDbContext();
		Guid before = await db.ReceiptItems.Where(x => x.Id == seed.Item).Select(x => x.NormalizationRevision).SingleAsync();
		Guid forged = Guid.NewGuid();
		await using var tx = await db.Database.BeginTransactionAsync();
		Func<Task> direct = () => db.Database.ExecuteSqlInterpolatedAsync($"SELECT matching.restore_source_revision('ReceiptItems', {seed.Item}, {before}, {forged})");
		(await direct.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("exclusive table lock");
		await tx.RollbackAsync();
		await using var forgedTx = await db.Database.BeginTransactionAsync();
		// A ledger row alone must not bypass the function's table-lock fence.
		await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO matching.\"SourceRevisionRestores\" (\"SourceTable\", \"SourceId\", \"PreviousRevision\", \"RestoredRevision\") VALUES ('ReceiptItems', {seed.Item}, {before}, {forged})");
		Func<Task> useForged = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"NormalizationRevision\" = {forged} WHERE \"Id\" = {seed.Item}");
		(await useForged.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("exclusive table lock");
		await forgedTx.RollbackAsync();
		(await db.ReceiptItems.Where(x => x.Id == seed.Item).Select(x => x.NormalizationRevision).SingleAsync()).Should().Be(before);
	}

	[Fact]
	public async Task HistoricalSourceRestoreAuditsCasConsumesOnceAndKeepsEvidenceImmutable()
	{
		var seed = await HistoricalSeedAsync();
		await using var db = fixture.CreateDbContext();
		Guid before = await db.ReceiptItems.Where(x => x.Id == seed.Item).Select(x => x.NormalizationRevision).SingleAsync();
		Guid restored = Guid.NewGuid();
		await using (var tx = await db.Database.BeginTransactionAsync())
		{
			await db.Database.ExecuteSqlRawAsync("LOCK TABLE receipts.\"ReceiptItems\" IN EXCLUSIVE MODE");
			await db.Database.ExecuteSqlInterpolatedAsync($"SELECT matching.restore_source_revision('ReceiptItems', {seed.Item}, {before}, {restored})");
			(await db.ReceiptItems.Where(x => x.Id == seed.Item).Select(x => x.NormalizationRevision).SingleAsync()).Should().Be(restored);
			(await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM matching.\"SourceRevisionRestores\" WHERE \"SourceId\" = {seed.Item} AND \"PreviousRevision\" = {before} AND \"RestoredRevision\" = {restored} AND \"Consumed\" AND \"Transaction\" = pg_current_xact_id()").SingleAsync()).Should().Be(1);
			await tx.CommitAsync();
		}
		foreach (string sql in new[]
		{
			"UPDATE matching.\"SourceRevisionRestores\" SET \"Consumed\" = false WHERE \"SourceId\" = {0}",
			"UPDATE matching.\"SourceRevisionRestores\" SET \"RestoredRevision\" = gen_random_uuid() WHERE \"SourceId\" = {0}",
			"DELETE FROM matching.\"SourceRevisionRestores\" WHERE \"SourceId\" = {0}",
			"TRUNCATE matching.\"SourceRevisionRestores\""
		})
		{
			Func<Task> tamper = () => db.Database.ExecuteSqlRawAsync(sql, seed.Item);
			(await tamper.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("evidence is immutable");
		}
		await using (var tx = await db.Database.BeginTransactionAsync())
		{
			await db.Database.ExecuteSqlRawAsync("LOCK TABLE receipts.\"ReceiptItems\" IN EXCLUSIVE MODE");
			Func<Task> stale = () => db.Database.ExecuteSqlInterpolatedAsync($"SELECT matching.restore_source_revision('ReceiptItems', {seed.Item}, {before}, {Guid.NewGuid()})");
			(await stale.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("compare-and-swap failed");
			await tx.RollbackAsync();
		}
		(await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM matching.\"SourceRevisionRestores\" WHERE \"SourceId\" = {seed.Item}").SingleAsync()).Should().Be(1);
		// Replaying a consumed grant cannot restore a previous token.
		await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"NormalizationRevision\" = {before} WHERE \"Id\" = {seed.Item}");
		(await db.ReceiptItems.Where(x => x.Id == seed.Item).Select(x => x.NormalizationRevision).SingleAsync()).Should().NotBe(before).And.NotBe(restored);
	}

	[Fact]
	public async Task HistoricalSourceRestoreEqualTokensPreserveTheRequestedToken()
	{
		var seed = await HistoricalSeedAsync();
		await using var db = fixture.CreateDbContext();
		Guid before = await db.ReceiptItems.Where(x => x.Id == seed.Item).Select(x => x.NormalizationRevision).SingleAsync();
		await using var tx = await db.Database.BeginTransactionAsync();
		await db.Database.ExecuteSqlRawAsync("LOCK TABLE receipts.\"ReceiptItems\" IN EXCLUSIVE MODE");
		await db.Database.ExecuteSqlInterpolatedAsync($"SELECT matching.restore_source_revision('ReceiptItems', {seed.Item}, {before}, {before})");
		(await db.ReceiptItems.Where(x => x.Id == seed.Item).Select(x => x.NormalizationRevision).SingleAsync()).Should().Be(before);
		await tx.RollbackAsync();
	}
}

