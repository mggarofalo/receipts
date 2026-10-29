using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LegacyV13Restore_RetainsAutomaticEvidenceAndFencesPriorWork(bool undoing)
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		var subject = new ClassificationTests(isolated);
		var captured = await subject.CaptureExecutableAsync(extraItems: 2);
		var undo = new HistoricalUndoCommand(captured.Request.TaxonomyRevisionId, "Keep frozen undo");
		if (undoing)
		{
			await subject.ApprovedExecutor().ApplyChunkAsync(subject.CurrentRestoreGeneration(), Actor, captured.Request.OperationId, 3);
			await subject.ApprovedExecutor().UndoChunkAsync(subject.CurrentRestoreGeneration(), Actor, captured.Request.OperationId, undo, 1);
		}
		string path = await subject.LegacyV13WithoutAutomaticHistoryAsync();
		Guid generation = subject.CurrentRestoreGeneration();
		try
		{
			var importer = new BackupImportService(new Factory(isolated), NullLogger<BackupImportService>.Instance);
			await using (var input = File.OpenRead(path)) { await importer.ImportFromSqliteAsync(input, default); }
			Guid fresh = subject.CurrentRestoreGeneration();
			fresh.Should().NotBe(generation);
			(await Assert.ThrowsAsync<TaxonomyException>(() => subject.ApprovedExecutor().ApplyChunkAsync(generation, Actor, captured.Request.OperationId, 1))).Status.Should().Be(409);
			(await Assert.ThrowsAsync<TaxonomyException>(() => subject.ApprovedExecutor().UndoChunkAsync(generation, Actor, captured.Request.OperationId, undo, 1))).Status.Should().Be(409);
			await using var db = isolated.CreateDbContext();
			var header = await db.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == captured.Request.OperationId);
			var rows = await db.HistoricalOperationItems.AsNoTracking().Where(x => x.OperationId == header.Id).ToListAsync();
			rows.Should().HaveCount(3);
			header.Kind.Should().Be(HistoricalOperationKind.ApprovedMappingV1);
			if (undoing)
			{
				header.State.Should().Be(HistoricalOperationState.Undoing);
				header.UndoReason.Should().Be(undo.Reason);
				rows.Count(x => x.UndoState == HistoricalItemState.Applied).Should().Be(1);
				(await subject.ApprovedExecutor().UndoChunkAsync(fresh, Actor, header.Id, undo, 2)).Undone.Should().Be(3);
			}
			else
			{
				header.State.Should().Be(HistoricalOperationState.Cancelled);
				rows.Should().OnlyContain(x => x.State == HistoricalItemState.Pending);
				(await subject.ApprovedExecutor().ApplyChunkAsync(fresh, Actor, header.Id, 3)).Applied.Should().Be(0);
			}
		}
		finally { File.Delete(path); }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LegacyV13Restore_RejectsMalformedEvidenceWithoutChangingDestination(bool malformedDestination)
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		var subject = new ClassificationTests(isolated);
		var captured = await subject.CaptureExecutableAsync();
		string path = await subject.LegacyV13WithoutAutomaticHistoryAsync();
		Guid generation = subject.CurrentRestoreGeneration();
		try
		{
			if (malformedDestination)
			{
				await using var db = isolated.CreateDbContext();
				await db.Database.OpenConnectionAsync();
				await db.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
				try
				{
					await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE taxonomy.\"HistoricalOperations\" SET \"RequestHash\"='broken' WHERE \"Id\"={captured.Request.OperationId}");
				}
				finally { await db.Database.ExecuteSqlRawAsync("SET session_replication_role = origin"); }
			}
			else
			{
				await using var sqlite = new SqliteConnection($"Data Source={path};Pooling=False");
				await sqlite.OpenAsync();
				await using var command = sqlite.CreateCommand();
				command.CommandText = "INSERT INTO historical_operation_archive(kind,id,payload) VALUES('operation',$id,$payload)";
				command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
				command.Parameters.AddWithValue("$payload", "{\"Kind\":1,\"ApprovedMappingRevisionId\":null}");
				await command.ExecuteNonQueryAsync();
			}
			var importer = new BackupImportService(new Factory(isolated), NullLogger<BackupImportService>.Instance);
			await using (var input = File.OpenRead(path))
			{
				await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportFromSqliteAsync(input, default));
			}
			subject.CurrentRestoreGeneration().Should().Be(generation);
			await using var after = isolated.CreateDbContext();
			(await after.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == captured.Request.OperationId)).State.Should().Be(HistoricalOperationState.Preview);
			(await after.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == captured.Request.OperationId)).State.Should().Be(HistoricalItemState.Pending);
			(await subject.Service().HistoryAsync(captured.Item, null, 10)).Items.Should().ContainSingle().Which.Id.Should().Be(captured.Assignment);
		}
		finally { File.Delete(path); }
	}

	private async Task<string> LegacyV13WithoutAutomaticHistoryAsync()
	{
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		await using var sqlite = new SqliteConnection($"Data Source={path};Pooling=False");
		await sqlite.OpenAsync();
		await using var command = sqlite.CreateCommand();
		command.CommandText = "UPDATE backup_metadata SET value='13' WHERE key='export_version'; DELETE FROM historical_operation_archive; DROP TABLE review_disposition_archive; DROP TABLE review_preference_archive";
		await command.ExecuteNonQueryAsync();
		return path;
	}
}
