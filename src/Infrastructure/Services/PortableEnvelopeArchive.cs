using System.Globalization;
using Domain.Envelopes;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Envelopes;
using Infrastructure.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>
/// Format 15 envelope tables. Item and receipt choices live in side tables so the format 1–14
/// receipt schemas, and the offline tools that read them, are unchanged. Restoring an older
/// format derives envelopes with <see cref="EnvelopeBackfill"/>, exactly as the deploy data step does.
/// </summary>
internal static class PortableEnvelopeArchive
{
	public const int Version = 15;

	public static IReadOnlyList<string> Tables { get; } = Array.AsReadOnly([
		"envelopes", "occasions", "item_type_envelope_defaults", "ynab_envelope_mappings",
		"receipt_item_envelopes", "receipt_envelope_choices", "envelope_backfill"]);

	public static IReadOnlyList<string> Ddl { get; } = Array.AsReadOnly([
		"""
		CREATE TABLE envelopes (
			id TEXT NOT NULL PRIMARY KEY,
			name TEXT NOT NULL,
			is_active INTEGER NOT NULL,
			deleted_at TEXT,
			deleted_by_user_id TEXT,
			deleted_by_api_key_id TEXT
		)
		""",
		"""
		CREATE TABLE occasions (
			id TEXT NOT NULL PRIMARY KEY,
			name TEXT NOT NULL,
			is_active INTEGER NOT NULL,
			deleted_at TEXT,
			deleted_by_user_id TEXT,
			deleted_by_api_key_id TEXT
		)
		""",
		"""
		CREATE TABLE item_type_envelope_defaults (
			id TEXT NOT NULL PRIMARY KEY,
			category_id TEXT NOT NULL UNIQUE,
			envelope_id TEXT NOT NULL,
			created_at TEXT NOT NULL,
			updated_at TEXT NOT NULL,
			FOREIGN KEY (envelope_id) REFERENCES envelopes(id)
		)
		""",
		"""
		CREATE TABLE ynab_envelope_mappings (
			id TEXT NOT NULL PRIMARY KEY,
			envelope_id TEXT NOT NULL,
			ynab_budget_id TEXT NOT NULL,
			ynab_category_id TEXT NOT NULL,
			ynab_category_name TEXT NOT NULL,
			ynab_category_group_name TEXT NOT NULL,
			created_at TEXT NOT NULL,
			updated_at TEXT NOT NULL,
			UNIQUE (envelope_id, ynab_budget_id),
			FOREIGN KEY (envelope_id) REFERENCES envelopes(id)
		)
		""",
		"""
		CREATE TABLE receipt_item_envelopes (
			receipt_item_id TEXT NOT NULL PRIMARY KEY,
			envelope_id TEXT,
			envelope_source TEXT NOT NULL,
			occasion_id TEXT,
			FOREIGN KEY (receipt_item_id) REFERENCES receipt_items(id),
			FOREIGN KEY (envelope_id) REFERENCES envelopes(id),
			FOREIGN KEY (occasion_id) REFERENCES occasions(id)
		)
		""",
		"""
		CREATE TABLE receipt_envelope_choices (
			receipt_id TEXT NOT NULL PRIMARY KEY,
			paid_from_envelope_id TEXT,
			occasion_id TEXT,
			FOREIGN KEY (receipt_id) REFERENCES receipts(id),
			FOREIGN KEY (paid_from_envelope_id) REFERENCES envelopes(id),
			FOREIGN KEY (occasion_id) REFERENCES occasions(id)
		)
		""",
		"""
		CREATE TABLE envelope_backfill (
			id INTEGER NOT NULL PRIMARY KEY,
			completed_at TEXT NOT NULL,
			item_count INTEGER NOT NULL,
			envelope_count INTEGER NOT NULL,
			default_count INTEGER NOT NULL
		)
		""",
	]);

	public static async Task ExportAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken ct)
	{
		foreach (EnvelopeEntity row in await source.Envelopes.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToListAsync(ct))
		{
			await InsertAsync(sqlite, "INSERT INTO envelopes VALUES ($id, $name, $active, $deletedAt, $deletedBy, $deletedByKey)", ct,
				("$id", row.Id.ToString()), ("$name", row.Name), ("$active", row.IsActive ? 1 : 0),
				("$deletedAt", row.DeletedAt?.ToString("O")), ("$deletedBy", row.DeletedByUserId), ("$deletedByKey", row.DeletedByApiKeyId?.ToString()));
		}
		foreach (OccasionEntity row in await source.Occasions.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToListAsync(ct))
		{
			await InsertAsync(sqlite, "INSERT INTO occasions VALUES ($id, $name, $active, $deletedAt, $deletedBy, $deletedByKey)", ct,
				("$id", row.Id.ToString()), ("$name", row.Name), ("$active", row.IsActive ? 1 : 0),
				("$deletedAt", row.DeletedAt?.ToString("O")), ("$deletedBy", row.DeletedByUserId), ("$deletedByKey", row.DeletedByApiKeyId?.ToString()));
		}
		foreach (ItemTypeEnvelopeDefaultEntity row in await source.ItemTypeEnvelopeDefaults.AsNoTracking().OrderBy(row => row.Id).ToListAsync(ct))
		{
			await InsertAsync(sqlite, "INSERT INTO item_type_envelope_defaults VALUES ($id, $category, $envelope, $created, $updated)", ct,
				("$id", row.Id.ToString()), ("$category", row.CategoryId.ToString()), ("$envelope", row.EnvelopeId.ToString()),
				("$created", row.CreatedAt.ToString("O")), ("$updated", row.UpdatedAt.ToString("O")));
		}
		foreach (YnabEnvelopeMappingEntity row in await source.YnabEnvelopeMappings.AsNoTracking().OrderBy(row => row.Id).ToListAsync(ct))
		{
			await InsertAsync(sqlite, "INSERT INTO ynab_envelope_mappings VALUES ($id, $envelope, $budget, $category, $name, $group, $created, $updated)", ct,
				("$id", row.Id.ToString()), ("$envelope", row.EnvelopeId.ToString()), ("$budget", row.YnabBudgetId),
				("$category", row.YnabCategoryId), ("$name", row.YnabCategoryName), ("$group", row.YnabCategoryGroupName),
				("$created", row.CreatedAt.ToString("O")), ("$updated", row.UpdatedAt.ToString("O")));
		}
		var items = await source.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking().OrderBy(row => row.Id)
			.Select(row => new { row.Id, row.EnvelopeId, row.EnvelopeSource, row.OccasionId }).ToListAsync(ct);
		foreach (var row in items)
		{
			await InsertAsync(sqlite, "INSERT INTO receipt_item_envelopes VALUES ($id, $envelope, $source, $occasion)", ct,
				("$id", row.Id.ToString()), ("$envelope", row.EnvelopeId?.ToString()), ("$source", row.EnvelopeSource.ToString()),
				("$occasion", row.OccasionId?.ToString()));
		}
		var receipts = await source.Receipts.IgnoreQueryFilters().AsNoTracking()
			.Where(row => row.PaidFromEnvelopeId != null || row.OccasionId != null).OrderBy(row => row.Id)
			.Select(row => new { row.Id, row.PaidFromEnvelopeId, row.OccasionId }).ToListAsync(ct);
		foreach (var row in receipts)
		{
			await InsertAsync(sqlite, "INSERT INTO receipt_envelope_choices VALUES ($id, $paidFrom, $occasion)", ct,
				("$id", row.Id.ToString()), ("$paidFrom", row.PaidFromEnvelopeId?.ToString()), ("$occasion", row.OccasionId?.ToString()));
		}
		if (await source.EnvelopeBackfills.AsNoTracking().SingleOrDefaultAsync(ct) is { } marker)
		{
			await InsertAsync(sqlite, "INSERT INTO envelope_backfill VALUES (1, $completed, $items, $envelopes, $defaults)", ct,
				("$completed", marker.CompletedAt.ToString("O")), ("$items", marker.ItemCount),
				("$envelopes", marker.EnvelopeCount), ("$defaults", marker.DefaultCount));
		}
	}

	/// <summary>Older formats carry no envelopes; they are derived only into a destination that has none.</summary>
	public static async Task RequireDerivableDestinationAsync(ApplicationDbContext db, int exportVersion, CancellationToken ct)
	{
		if (exportVersion < Version && (await db.Envelopes.IgnoreQueryFilters().AnyAsync(ct) || await db.ItemTypeEnvelopeDefaults.AnyAsync(ct)))
		{
			throw new InvalidOperationException($"Restoring a format {exportVersion} backup derives envelopes from its YNAB mappings and requires a destination without envelopes. Restore it into a fresh database.");
		}
	}

	/// <summary>Rows written per table; a derived restore creates every row.</summary>
	public sealed record ImportCounts(int EnvelopesCreated, int EnvelopesUpdated, int OccasionsCreated, int OccasionsUpdated,
		int DefaultsCreated, int DefaultsUpdated, int YnabMappingsCreated, int YnabMappingsUpdated);

	public static async Task<ImportCounts> ImportAsync(ApplicationDbContext db, SqliteConnection sqlite, int exportVersion, CancellationToken ct)
	{
		if (exportVersion < Version)
		{
			// The destination had none (RequireDerivableDestinationAsync), so everything present was derived now.
			await DeriveAsync(db, sqlite, ct);
			return new(await db.Envelopes.IgnoreQueryFilters().CountAsync(ct), 0, 0, 0,
				await db.ItemTypeEnvelopeDefaults.CountAsync(ct), 0, await db.YnabEnvelopeMappings.CountAsync(ct), 0);
		}

		int envelopesCreated = 0, envelopesUpdated = 0, occasionsCreated = 0, occasionsUpdated = 0;
		int defaultsCreated = 0, defaultsUpdated = 0, mappingsCreated = 0, mappingsUpdated = 0;
		foreach (var row in await ReadAsync(sqlite, "SELECT id, name, is_active, deleted_at, deleted_by_user_id, deleted_by_api_key_id FROM envelopes", ct))
		{
			Guid id = Id(row[0]);
			EnvelopeEntity? existing = await db.Envelopes.IgnoreQueryFilters().SingleOrDefaultAsync(e => e.Id == id, ct);
			if (existing == null) { envelopesCreated++; } else { envelopesUpdated++; }
			EnvelopeEntity entity = existing ?? db.Envelopes.Add(new() { Id = id }).Entity;
			entity.Name = Text(row[1]);
			entity.IsActive = Convert.ToInt64(row[2], CultureInfo.InvariantCulture) != 0;
			Delete(entity, row[3], row[4], row[5]);
		}
		foreach (var row in await ReadAsync(sqlite, "SELECT id, name, is_active, deleted_at, deleted_by_user_id, deleted_by_api_key_id FROM occasions", ct))
		{
			Guid id = Id(row[0]);
			OccasionEntity? existing = await db.Occasions.IgnoreQueryFilters().SingleOrDefaultAsync(e => e.Id == id, ct);
			if (existing == null) { occasionsCreated++; } else { occasionsUpdated++; }
			OccasionEntity entity = existing ?? db.Occasions.Add(new() { Id = id }).Entity;
			entity.Name = Text(row[1]);
			entity.IsActive = Convert.ToInt64(row[2], CultureInfo.InvariantCulture) != 0;
			Delete(entity, row[3], row[4], row[5]);
		}
		await db.SaveChangesAsync(ct);

		Dictionary<Guid, ReceiptItemEntity> items = await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes()
			.ToDictionaryAsync(item => item.Id, ct);
		foreach (var row in await ReadAsync(sqlite, "SELECT receipt_item_id, envelope_id, envelope_source, occasion_id FROM receipt_item_envelopes", ct))
		{
			ReceiptItemEntity item = items.GetValueOrDefault(Id(row[0]))
				?? throw new InvalidOperationException("Envelope choice references a receipt item absent from the backup.");
			item.EnvelopeId = OptionalId(row[1]);
			item.EnvelopeSource = Enum.TryParse(Text(row[2]), ignoreCase: false, out EnvelopeSource source) && Enum.IsDefined(source)
				? source : throw new InvalidOperationException("Invalid envelope source in backup.");
			item.OccasionId = OptionalId(row[3]);
		}
		foreach (var row in await ReadAsync(sqlite, "SELECT receipt_id, paid_from_envelope_id, occasion_id FROM receipt_envelope_choices", ct))
		{
			Guid id = Id(row[0]);
			ReceiptEntity receipt = await db.Receipts.IgnoreQueryFilters().SingleOrDefaultAsync(r => r.Id == id, ct)
				?? throw new InvalidOperationException("Envelope choice references a receipt absent from the backup.");
			receipt.PaidFromEnvelopeId = OptionalId(row[1]);
			receipt.OccasionId = OptionalId(row[2]);
		}
		foreach (var row in await ReadAsync(sqlite, "SELECT id, category_id, envelope_id, created_at, updated_at FROM item_type_envelope_defaults", ct))
		{
			Guid category = Id(row[1]);
			ItemTypeEnvelopeDefaultEntity? existing = await db.ItemTypeEnvelopeDefaults.SingleOrDefaultAsync(e => e.CategoryId == category, ct);
			if (existing == null) { defaultsCreated++; } else { defaultsUpdated++; }
			ItemTypeEnvelopeDefaultEntity entity = existing
				?? db.ItemTypeEnvelopeDefaults.Add(new() { Id = Id(row[0]), CategoryId = category }).Entity;
			entity.EnvelopeId = Id(row[2]);
			entity.CreatedAt = Time(row[3]);
			entity.UpdatedAt = Time(row[4]);
		}
		foreach (var row in await ReadAsync(sqlite, "SELECT id, envelope_id, ynab_budget_id, ynab_category_id, ynab_category_name, ynab_category_group_name, created_at, updated_at FROM ynab_envelope_mappings", ct))
		{
			Guid envelope = Id(row[1]);
			string budget = Text(row[2]);
			YnabEnvelopeMappingEntity? existing = await db.YnabEnvelopeMappings.SingleOrDefaultAsync(e => e.EnvelopeId == envelope && e.YnabBudgetId == budget, ct);
			if (existing == null) { mappingsCreated++; } else { mappingsUpdated++; }
			YnabEnvelopeMappingEntity entity = existing
				?? db.YnabEnvelopeMappings.Add(new() { Id = Id(row[0]), EnvelopeId = envelope, YnabBudgetId = budget }).Entity;
			entity.YnabCategoryId = Text(row[3]);
			entity.YnabCategoryName = Text(row[4]);
			entity.YnabCategoryGroupName = Text(row[5]);
			entity.CreatedAt = Time(row[6]);
			entity.UpdatedAt = Time(row[7]);
		}
		foreach (var row in await ReadAsync(sqlite, "SELECT completed_at, item_count, envelope_count, default_count FROM envelope_backfill", ct))
		{
			EnvelopeBackfillEntity marker = await db.EnvelopeBackfills.SingleOrDefaultAsync(ct) ?? db.EnvelopeBackfills.Add(new() { Id = 1 }).Entity;
			marker.CompletedAt = Time(row[0]);
			marker.ItemCount = Convert.ToInt32(row[1], CultureInfo.InvariantCulture);
			marker.EnvelopeCount = Convert.ToInt32(row[2], CultureInfo.InvariantCulture);
			marker.DefaultCount = Convert.ToInt32(row[3], CultureInfo.InvariantCulture);
		}
		await db.SaveChangesAsync(ct);
		return new(envelopesCreated, envelopesUpdated, occasionsCreated, occasionsUpdated,
			defaultsCreated, defaultsUpdated, mappingsCreated, mappingsUpdated);
	}

	private static async Task DeriveAsync(ApplicationDbContext db, SqliteConnection sqlite, CancellationToken ct)
	{
		// Every restored purchase is historical; the backfill records the envelope it pushes to today.
		// Partial legacy backups may carry no purchases at all.
		HashSet<Guid> restored = BackupImportService.TableExists(sqlite, "receipt_items")
			? (await ReadAsync(sqlite, "SELECT id FROM receipt_items", ct)).Select(row => Id(row[0])).ToHashSet()
			: [];
		foreach (ReceiptItemEntity item in await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes()
			.Where(item => item.EnvelopeId == null).ToListAsync(ct))
		{
			if (restored.Contains(item.Id))
			{
				item.EnvelopeSource = EnvelopeSource.Historical;
			}
		}
		await db.SaveChangesAsync(ct);
		await EnvelopeBackfill.RunAsync(db, TimeProvider.System, ct);
	}

	private static void Delete(ISoftDeletable entity, object? deletedAt, object? deletedBy, object? deletedByKey)
	{
		entity.DeletedAt = deletedAt is string at ? Time(at) : null;
		entity.DeletedByUserId = deletedBy as string;
		entity.DeletedByApiKeyId = OptionalId(deletedByKey);
		entity.CascadeDeletedByParentId = null;
	}

	private static async Task<List<object?[]>> ReadAsync(SqliteConnection sqlite, string sql, CancellationToken ct)
	{
		await using SqliteCommand command = sqlite.CreateCommand();
		command.CommandText = sql;
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
		List<object?[]> rows = [];
		while (await reader.ReadAsync(ct))
		{
			object?[] row = new object?[reader.FieldCount];
			for (int index = 0; index < row.Length; index++)
			{
				row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
			}
			rows.Add(row);
		}
		return rows;
	}

	private static async Task InsertAsync(SqliteConnection sqlite, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
	{
		await using SqliteCommand command = sqlite.CreateCommand();
		command.CommandText = sql;
		foreach ((string name, object? value) in parameters)
		{
			command.Parameters.AddWithValue(name, value ?? DBNull.Value);
		}
		await command.ExecuteNonQueryAsync(ct);
	}

	private static string Text(object? value) => value as string ?? throw new InvalidOperationException("Missing envelope archive text.");

	private static Guid Id(object? value) => Guid.TryParse(value as string, out Guid id) && id != Guid.Empty
		? id : throw new InvalidOperationException("Invalid identifier in envelope archive.");

	private static Guid? OptionalId(object? value) => value is null ? null : Id(value);

	private static DateTimeOffset Time(object? value) => DateTimeOffset.TryParse(value as string, CultureInfo.InvariantCulture,
		DateTimeStyles.RoundtripKind, out DateTimeOffset time) ? time : throw new InvalidOperationException("Invalid timestamp in envelope archive.");
}
