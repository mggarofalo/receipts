using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	internal sealed record SourceRevision(string Table, Guid Id, Guid Revision);

	private static async Task ExportSourceRevisionsAsync(ApplicationDbContext db, SqliteConnection sqlite, CancellationToken ct)
	{
		await using var schema = sqlite.CreateCommand();
		schema.CommandText = "CREATE TABLE source_revision_archive(kind TEXT NOT NULL, id TEXT NOT NULL, revision TEXT NOT NULL, PRIMARY KEY(kind,id))";
		await schema.ExecuteNonQueryAsync(ct);
		var receipts = await db.Receipts.IgnoreQueryFilters().AsNoTracking().Select(x => new SourceRevision("Receipts", x.Id, x.NormalizationRevision)).ToListAsync(ct);
		var items = await db.ReceiptItems.IgnoreQueryFilters().AsNoTracking().Select(x => new SourceRevision("ReceiptItems", x.Id, x.NormalizationRevision)).ToListAsync(ct);
		foreach (var row in receipts.Concat(items))
		{
			await using var command = sqlite.CreateCommand();
			command.CommandText = "INSERT INTO source_revision_archive VALUES($kind,$id,$revision)";
			command.Parameters.AddWithValue("$kind", row.Table);
			command.Parameters.AddWithValue("$id", row.Id.ToString());
			command.Parameters.AddWithValue("$revision", row.Revision.ToString());
			await command.ExecuteNonQueryAsync(ct);
		}
	}

	private static async Task<State> ReadSourceRevisionsAsync(SqliteConnection sqlite, State state, CancellationToken ct)
	{
		List<SourceRevision> rows = [];
		await using (var command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT kind,id,revision FROM source_revision_archive";
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				Require(Guid.TryParse(reader.GetString(1), out Guid id) && id != Guid.Empty
					&& Guid.TryParse(reader.GetString(2), out Guid revision) && revision != Guid.Empty, "Invalid source revision.");
				rows.Add(new(reader.GetString(0), id, Guid.Parse(reader.GetString(2))));
			}
		}
		Require(rows.All(x => x.Table is "Receipts" or "ReceiptItems") && rows.Select(x => (x.Table, x.Id)).Distinct().Count() == rows.Count, "Invalid source revision keys.");
		foreach (var (table, kind) in new[] { ("receipts", "Receipts"), ("receipt_items", "ReceiptItems") })
		{
			await using var command = sqlite.CreateCommand();
			command.CommandText = $"SELECT id FROM {table}";
			HashSet<Guid> ids = [];
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct)) { ids.Add(Guid.Parse(reader.GetString(0))); }
			Require(ids.SetEquals(rows.Where(x => x.Table == kind).Select(x => x.Id)), "Source revision coverage differs from business snapshot.");
		}
		return state with { SourceRevisions = rows };
	}

	private static async Task PreflightSourceRevisionsAsync(ApplicationDbContext db, State incoming, CancellationToken ct)
	{
		var receipts = await db.Receipts.IgnoreQueryFilters().AsNoTracking().Select(x => new { x.Id, x.NormalizationRevision }).ToDictionaryAsync(x => x.Id, x => x.NormalizationRevision, ct);
		var items = await db.ReceiptItems.IgnoreQueryFilters().AsNoTracking().Select(x => new { x.Id, x.NormalizationRevision }).ToDictionaryAsync(x => x.Id, x => x.NormalizationRevision, ct);
		foreach (var row in incoming.SourceRevisions)
		{
			var revisions = row.Table == "Receipts" ? receipts : items;
			Require(!revisions.TryGetValue(row.Id, out Guid revision) || revision == row.Revision, "Restore conflicts with destination source revision.");
		}
	}

	internal static async Task RestoreSourceRevisionsAsync(ApplicationDbContext db, State incoming, CancellationToken ct)
	{
		// This runs after every business/projection/deletion save, while the restore's
		// exclusive source table locks and transaction remain held. Frozen manifests
		// are never rewritten. Normal updates continue rotating their source tokens.
		foreach (var row in incoming.SourceRevisions)
		{
			Guid current = row.Table == "Receipts"
				? await db.Receipts.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == row.Id).Select(x => x.NormalizationRevision).SingleAsync(ct)
				: await db.ReceiptItems.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == row.Id).Select(x => x.NormalizationRevision).SingleAsync(ct);
			if (current == row.Revision) { continue; }
			await db.Database.ExecuteSqlInterpolatedAsync($"SELECT matching.restore_source_revision({row.Table}, {row.Id}, {current}, {row.Revision})", ct);
		}
	}
}
