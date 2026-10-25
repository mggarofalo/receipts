using System.Text.Json;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;

namespace Infrastructure.IntegrationTests.Fixtures;

// Completes handwritten business rows with the current authoritative taxonomy and
// explicit active/deleted state. Call only after all fixture SQL has been applied.
internal static class CurrentBackupFixture
{
	internal static async Task CompleteAsync(SqliteConnection sqlite, PostgresFixture source)
	{
		await using ApplicationDbContext context = source.CreateDbContext();
		await PortableTaxonomyArchive.ExportAsync(context, sqlite, CancellationToken.None);
		await using var command = sqlite.CreateCommand();
		command.CommandText = "DELETE FROM portable_deletion_state; CREATE TABLE normalization_catalog(kind TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(kind,id));";
		await command.ExecuteNonQueryAsync();
		foreach (string table in new[] { "categories", "subcategories", "item_templates", "receipts", "receipt_items", "transactions", "adjustments", "ynab_sync_records", "accepted_duplicate_pairs" })
		{
			command.CommandText = $"SELECT id FROM {table}";
			List<Guid> ids = [];
			await using (var reader = await command.ExecuteReaderAsync())
			{ while (await reader.ReadAsync()) { ids.Add(Guid.Parse(reader.GetString(0))); } }
			foreach (Guid id in ids)
			{
				command.CommandText = "INSERT INTO portable_deletion_state VALUES($kind,$id,$payload)";
				command.Parameters.Clear();
				command.Parameters.AddWithValue("$kind", table);
				command.Parameters.AddWithValue("$id", id.ToString());
				command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new PortableTaxonomyArchive.DeletionState(table, id, null, null, null, null)));
				await command.ExecuteNonQueryAsync();
				if (table == "receipt_items")
				{
					command.CommandText = "INSERT INTO normalization_catalog VALUES('assignment',$id,$payload)";
					command.Parameters.Clear();
					command.Parameters.AddWithValue("$id", id.ToString());
					command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new PortableCatalogArchive.Assignment(id, "legacy_unknown", null, null)));
					await command.ExecuteNonQueryAsync();
					command.CommandText = "INSERT INTO normalization_catalog VALUES('review_evidence',$id,$payload)";
					command.Parameters["$payload"].Value = JsonSerializer.Serialize(new PortableCatalogArchive.ReviewEvidence(id, Domain.NormalizedDescriptions.NormalizationReviewState.NeedsReview, null, null, null, null, null, null, null, null));
					await command.ExecuteNonQueryAsync();
				}
			}
			command.Parameters.Clear();
		}
		command.CommandText = "UPDATE normalization_catalog SET payload=json_set(payload,'$.Origin','unassigned') WHERE kind='assignment' AND id IN (SELECT id FROM receipt_items WHERE normalized_description_id IS NULL)";
		await command.ExecuteNonQueryAsync();
	}
}
