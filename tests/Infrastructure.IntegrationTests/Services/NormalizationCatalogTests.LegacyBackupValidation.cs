using System.Text.Json.Nodes;
using FluentAssertions;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationCatalogTests
{
	[Theory]
	[InlineData(7, "mapping", "Invalid catalog mapping.")]
	[InlineData(8, "mapping", "Invalid catalog mapping.")]
	[InlineData(8, "review_evidence", "Purchase review evidence disagrees with source or identity.")]
	public async Task LegacyPortableBackup_CorruptAuthorityKeepsImportRejectionContractAndRollsBack(
		int version, string kind, string reason)
	{
		await ResetAsync();
		var target = await CanonicalAsync("Reviewed legacy beef franks");
		var ids = await ItemsAsync(Source, 1);
		var service = Service();
		var preview = await service.PreviewAsync(Actor, Request(target), CancellationToken.None);
		await service.ApproveAsync(Actor, preview.Id, preview.Revision, CancellationToken.None);
		var facts = await FactsAsync(ids);
		var itemBefore = (await ReadAsync(ids)).Single();
		var mappingBefore = (await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Single();
		int auditCount;
		await using (var db = fixture.CreateDbContext()) { auditCount = await db.AuditLogs.CountAsync(); }
		string path = await new BackupService(new Factory(fixture), NullLogger<BackupService>.Instance).ExportToSqliteAsync();
		try
		{
			await using (var archive = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
			{
				await archive.OpenAsync();
				await using var command = archive.CreateCommand();
				command.CommandText = "UPDATE backup_metadata SET value=$version WHERE key='export_version'";
				command.Parameters.AddWithValue("$version", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
				(await command.ExecuteNonQueryAsync()).Should().Be(1);
				command.Parameters.Clear();
				// Version 7 predates purchase review evidence and receipt replay records.
				if (version == 7)
				{
					command.CommandText = "DELETE FROM normalization_catalog WHERE kind IN ('review_evidence','write_request')";
					await command.ExecuteNonQueryAsync();
				}
				command.CommandText = "SELECT payload FROM normalization_catalog WHERE kind=$kind";
				command.Parameters.AddWithValue("$kind", kind);
				var payload = JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!;
				if (version == 7) { payload.AsObject().Remove("ExclusionReason").Should().BeTrue(); }
				payload["SourceHash"] = new string('0', 64);
				command.CommandText = "UPDATE normalization_catalog SET payload=$payload WHERE kind=$kind";
				command.Parameters.AddWithValue("$payload", payload.ToJsonString());
				(await command.ExecuteNonQueryAsync()).Should().Be(1);
				command.Parameters.Clear();
				command.CommandText = "UPDATE receipt_items SET quantity='99'";
				(await command.ExecuteNonQueryAsync()).Should().Be(1);
			}

			// Historic archives are admitted only into fresh installations. Keep the
			// source untouched and prove rejection rolls back the isolated destination.
			var destination = new PostgresFixture();
			await destination.InitializeAsync();
			try
			{
				await using var stream = File.OpenRead(path);
				Func<Task> import = () => new BackupImportService(new Factory(destination), NullLogger<BackupImportService>.Instance)
					.ImportFromSqliteAsync(stream, CancellationToken.None);
				var rejection = await import.Should().ThrowAsync<InvalidOperationException>().WithMessage(reason);
				rejection.Which.InnerException.Should().BeOfType<InvalidDataException>().Which.Message.Should().Be(reason);
				await using var destinationRead = destination.CreateDbContext();
				(await destinationRead.ReceiptItems.IgnoreQueryFilters().CountAsync()).Should().Be(0);
				(await destinationRead.NormalizationSourceMappings.CountAsync()).Should().Be(0);
				(await destinationRead.LegacyMigrations.CountAsync()).Should().Be(0);
				(await destinationRead.TaxonomyRevisions.CountAsync()).Should().Be(1);
			}
			finally { await destination.DisposeAsync(); }
			(await FactsAsync(ids)).Should().BeEquivalentTo(facts);
			var itemAfter = (await ReadAsync(ids)).Single();
			itemAfter.NormalizationAssignmentId.Should().Be(itemBefore.NormalizationAssignmentId);
			itemAfter.NormalizationReviewDecisionId.Should().Be(itemBefore.NormalizationReviewDecisionId);
			itemAfter.NormalizationReviewState.Should().Be(itemBefore.NormalizationReviewState);
			(await service.ListMappingsAsync(Actor, Source, CancellationToken.None)).Single().Should().BeEquivalentTo(mappingBefore);
			(await service.GetOperationAsync(Actor, preview.Id, CancellationToken.None))!.State.Should().Be("completed");
			await using var verify = fixture.CreateDbContext();
			(await verify.AuditLogs.CountAsync()).Should().Be(auditCount);
		}
		finally { File.Delete(path); }
	}
}
