using System.Security.Cryptography;
using System.Text.Json;
using Application.Taxonomy;
using Infrastructure.Entities.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Infrastructure.Services;

public partial class BackupImportService
{
	private sealed record LegacyImportPreparation(LegacyMigrationRequest Source, bool IsReplay);
	private const string LegacyBackupActor = "legacy_backup_import";

	// Capture only the source file. Destination suggestions must never alter imported meaning.
	private static async Task<LegacyMigrationRequest> CaptureLegacyTaxonomyAsync(
		SqliteConnection sqlite, string path, int version, CancellationToken ct)
	{
		await using FileStream file = File.OpenRead(path);
		byte[] fingerprint = await SHA256.HashDataAsync(file, ct);
		string dataset = "portable-v" + version + ":sha256:" + Convert.ToHexString(fingerprint);
		byte[] operationBytes = fingerprint[..16];
		operationBytes[6] = (byte)((operationBytes[6] & 0x0f) | 0x80);
		operationBytes[8] = (byte)((operationBytes[8] & 0x3f) | 0x80);
		Guid operation = new(operationBytes, bigEndian: true);
		List<LegacyCategoryEvidence> categories = [];
		List<LegacySubcategoryEvidence> subcategories = [];
		List<LegacyItemEvidence> items = [];
		LegacyDeletionState active = new(); // v1-9 contain no portable trash metadata.
		long count = 0;
		foreach (string table in new[] { "categories", "subcategories", "receipt_items" })
		{
			if (!TableExists(sqlite, table)) { continue; }
			await using SqliteCommand counter = sqlite.CreateCommand();
			counter.CommandText = "SELECT COUNT(*) FROM " + table;
			count += Convert.ToInt64(await counter.ExecuteScalarAsync(ct));
		}
		if (count > LegacyTaxonomyMigrationService.MaximumSourceRows)
		{ throw new InvalidOperationException("Legacy backup exceeds the bounded initial taxonomy migration row limit."); }
		if (TableExists(sqlite, "categories"))
		{
			await using SqliteCommand command = sqlite.CreateCommand();
			command.CommandText = "SELECT id, name, description, is_active FROM categories";
			await using SqliteDataReader rows = await command.ExecuteReaderAsync(ct);
			while (await rows.ReadAsync(ct))
			{ categories.Add(new(Guid.Parse(rows.GetString(0)), NullableText(rows, 1), NullableText(rows, 2), rows.GetBoolean(3), active)); }
		}
		if (TableExists(sqlite, "subcategories"))
		{
			await using SqliteCommand command = sqlite.CreateCommand();
			command.CommandText = "SELECT id, category_id, name, description, is_active FROM subcategories";
			await using SqliteDataReader rows = await command.ExecuteReaderAsync(ct);
			while (await rows.ReadAsync(ct))
			{ subcategories.Add(new(Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)), NullableText(rows, 2), NullableText(rows, 3), rows.GetBoolean(4), active)); }
		}
		HashSet<Guid> receipts = await FileIdsAsync(sqlite, "receipts", ct);
		HashSet<Guid> products = version >= 5 ? await FileIdsAsync(sqlite, "normalized_descriptions", ct) : [];
		if (TableExists(sqlite, "receipt_items"))
		{
			await using SqliteCommand command = sqlite.CreateCommand();
			command.CommandText = "SELECT id, receipt_id, category, subcategory, total_amount, total_amount_currency"
				+ (version >= 5 ? ", normalized_description_id" : "") + " FROM receipt_items";
			await using SqliteDataReader rows = await command.ExecuteReaderAsync(ct);
			while (await rows.ReadAsync(ct))
			{
				Guid receipt = Guid.Parse(rows.GetString(1));
				if (!receipts.Contains(receipt)) { throw new InvalidOperationException("Legacy backup item references a missing source receipt."); }
				Guid? product = version >= 5 && !rows.IsDBNull(6) ? Guid.Parse(rows.GetString(6)) : null;
				items.Add(new(Guid.Parse(rows.GetString(0)), receipt, NullableText(rows, 2), NullableText(rows, 3),
					active, active, rows.GetDecimal(4), ParseCurrency(rows.GetString(5)).ToString(), product,
					product.HasValue && !products.Contains(product.Value) ? "missing_product" : null));
			}
		}
		DateTimeOffset now = DateTimeOffset.UtcNow;
		DateTimeOffset observed = new(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
		return new(dataset, operation, LegacyTaxonomyMigrationService.InitialOperationKind, LegacyBackupActor,
			observed, categories, subcategories, items);
	}

	private static string? NullableText(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

	private static async Task<HashSet<Guid>> FileIdsAsync(SqliteConnection sqlite, string table, CancellationToken ct)
	{
		HashSet<Guid> result = [];
		if (!TableExists(sqlite, table)) { return result; }
		await using SqliteCommand command = sqlite.CreateCommand();
		command.CommandText = "SELECT id FROM " + table;
		await using SqliteDataReader rows = await command.ExecuteReaderAsync(ct);
		while (await rows.ReadAsync(ct)) { result.Add(Guid.Parse(rows.GetString(0))); }
		return result;
	}

	// Must run before normalization fencing or ANY business mutation, regardless of rollout flags.
	private static async Task<LegacyImportPreparation> PrepareLegacyTaxonomyAsync(
		ApplicationDbContext db, LegacyMigrationRequest source, CancellationToken ct)
	{
		if (db.Database.CurrentTransaction is null)
		{ throw new InvalidOperationException("Legacy backup admission requires an active transaction."); }
		await LegacyTaxonomyMigrationService.LockAsync(db, ct);
		var prior = await db.LegacyMigrations.AsNoTracking().SingleOrDefaultAsync(row => row.Actor == source.Actor
			&& row.OperationId == source.OperationId && row.OperationKind == source.OperationKind, ct);
		if (prior != null)
		{
			// Preserve the first actual import boundary. A repeat never rewinds later business edits.
			source = source with { ObservedAt = prior.ObservedAt };
			LegacyMigrationPlan expected = LegacyTaxonomyMigrationPlanner.Plan(source);
			if (prior.SourceDataset != source.SourceDataset || prior.RequestHash != expected.RequestHash
				|| prior.RevisionId != expected.RevisionId
				|| JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(JsonSerializer.Deserialize<LegacyMigrationPlan>(prior.PlanJson)))
			{ throw new LegacyMigrationReplayException(); }
			return new(source, true);
		}
		await LegacyTaxonomyMigrationService.RequireGenesisAsync(db, ct);
		if (await db.ReceiptItems.IgnoreQueryFilters().AnyAsync(ct)
			|| !await HasOnlyUntouchedSeedSuggestionsAsync(db, ct))
		{
			throw new InvalidOperationException("Legacy backup conversion requires a fresh target with only untouched seed suggestions and no receipt-item rows. Restore into a fresh database; merging unrelated legacy evidence requires explicit reviewed reconciliation.");
		}
		LegacyMigrationPlan plan = LegacyTaxonomyMigrationPlanner.Plan(source);
		if (!plan.CanCommit) { throw new InvalidOperationException("Legacy backup taxonomy preflight has blocking exceptions. Resolve the source evidence before importing."); }
		return new(source, false);
	}
	private static async Task<bool> HasOnlyUntouchedSeedSuggestionsAsync(ApplicationDbContext db, CancellationToken ct)
	{
		// Read seed definitions from their authoritative configurations, not a copied list of IDs.
		var model = db.GetService<IDesignTimeModel>().Model;
		var categories = model.FindEntityType(typeof(CategoryEntity))!.GetSeedData().ToDictionary(r => (Guid)r["Id"]!);
		var children = model.FindEntityType(typeof(SubcategoryEntity))!.GetSeedData().ToDictionary(r => (Guid)r["Id"]!);
		if (await db.Categories.IgnoreQueryFilters().CountAsync(ct) > categories.Count
			|| await db.Subcategories.IgnoreQueryFilters().CountAsync(ct) > children.Count) { return false; }
		var actualCategories = await db.Categories.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking().ToListAsync(ct);
		var actualChildren = await db.Subcategories.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking().ToListAsync(ct);
		return actualCategories.All(row => categories.TryGetValue(row.Id, out var seed)
				&& row.Name == (string?)seed["Name"] && row.Description == (string?)seed["Description"]
				&& row.IsActive == (bool)seed["IsActive"]! && row.DeletedAt == null && row.DeletedByUserId == null
				&& row.DeletedByApiKeyId == null && row.CascadeDeletedByParentId == null)
			&& actualChildren.All(row => children.TryGetValue(row.Id, out var seed)
				&& row.CategoryId == (Guid)seed["CategoryId"]! && row.Name == (string?)seed["Name"]
				&& row.Description == (string?)seed["Description"] && row.IsActive == (bool)seed["IsActive"]!
				&& row.DeletedAt == null && row.DeletedByUserId == null && row.DeletedByApiKeyId == null
				&& row.CascadeDeletedByParentId == null);
	}

}
