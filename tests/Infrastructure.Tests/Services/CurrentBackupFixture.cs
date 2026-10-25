using System.Text.Json;
using Infrastructure.Configurations;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

internal static class CurrentBackupFixture
{
	internal static void Seed(ApplicationDbContext source)
	{
		if (source.TaxonomyHeads.Any())
		{
			return;
		}

		Guid genesis = TaxonomyConfiguration.GenesisId;
		source.TaxonomyRevisions.Add(new TaxonomyRevisionEntity
		{
			Id = genesis,
			Actor = "system",
			OperationId = genesis,
			Reason = "Empty taxonomy foundation; legacy import is separate.",
			RecordedAt = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero)
		});
		source.TaxonomyHeads.Add(new() { Id = 1, RevisionId = genesis });
		source.SaveChanges();
	}

	internal static async Task CompleteAsync(SqliteConnection sqlite)
	{
		var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase($"portable-fixture-{Guid.NewGuid()}").Options;
		await using var source = new ApplicationDbContext(options);
		Seed(source);
		using (var items = sqlite.CreateCommand())
		{
			items.CommandText = "SELECT id,normalized_description_id FROM receipt_items";
			using var rows = await items.ExecuteReaderAsync();
			while (await rows.ReadAsync())
			{
				source.ReceiptItems.Add(new() { Id = Guid.Parse(rows.GetString(0)), Description = "fixture catalog projection", Category = "", NormalizationOrigin = rows.IsDBNull(1) ? "unassigned" : "legacy_unknown", NormalizationAssignmentId = Guid.NewGuid() });
			}
		}
		await source.SaveChangesAsync();
		await PortableCatalogArchive.ExportAsync(source, sqlite, CancellationToken.None);
		await PortableTaxonomyArchive.ExportAsync(source, sqlite, CancellationToken.None);
		// Each included business row owns explicit active deletion state; never borrow destination parents.
		foreach (string table in new[] { "categories", "subcategories", "item_templates", "receipts", "receipt_items", "transactions", "adjustments", "ynab_sync_records", "accepted_duplicate_pairs" })
		{
			List<Guid> ids = [];
			using (var read = sqlite.CreateCommand())
			{
				read.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$table";
				read.Parameters.AddWithValue("$table", table);
				if (Convert.ToInt32(read.ExecuteScalar()) == 0)
				{
					continue;
				}

				read.Parameters.Clear();
				read.CommandText = $"SELECT id FROM {table}";
				using var rows = await read.ExecuteReaderAsync();
				while (await rows.ReadAsync())
				{
					ids.Add(Guid.Parse(rows.GetString(0)));
				}
			}
			foreach (Guid id in ids)
			{
				using var write = sqlite.CreateCommand();
				write.CommandText = "INSERT OR REPLACE INTO portable_deletion_state VALUES($kind,$id,$payload)";
				write.Parameters.AddWithValue("$kind", table); write.Parameters.AddWithValue("$id", id.ToString());
				write.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new PortableTaxonomyArchive.DeletionState(table, id, null, null, null, null)));
				await write.ExecuteNonQueryAsync();
			}
		}
	}
}
