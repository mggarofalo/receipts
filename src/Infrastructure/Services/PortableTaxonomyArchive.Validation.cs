using System.Text.Json;
using Application.Taxonomy;
using Infrastructure.Entities.Taxonomy;
using Microsoft.Data.Sqlite;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0');
	private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
	private static string ArchiveKey<T>(T value) => JsonSerializer.Serialize(value switch
	{
		HistoricalOperationEntity r => r.Id,
		HistoricalOperationItemEntity r => new { r.OperationId, r.ItemId },
		CategoryMappingRevisionEntity r => r.Id,
		CategoryMappingHeadEntity r => r.SourceFingerprint,
		TaxonomyIdentityEntity r => (object)r.Id,
		TaxonomyHeadEntity r => r.Id,
		TaxonomyRevisionEntity r => r.Id,
		TaxonomyNodeEntity r => new { r.RevisionId, r.CategoryId },
		TaxonomyOperationEntity r => new { r.Actor, r.OperationId },
		PurchaseClassificationEntity r => r.Id,
		CurrentClassificationEntity r => r.ReceiptItemId,
		ClassificationOperationEntity r => new { r.Actor, r.OperationId },
		LegacyMigrationEntity r => new { r.Actor, r.OperationId, r.OperationKind },
		LegacyMigrationCrosswalkEntity r => new { r.Actor, r.OperationId, r.SourceTable, r.SourceId },
		LegacyMigrationItemEntity r => new { r.Actor, r.OperationId, r.ReceiptItemId },
		_ => throw new InvalidDataException("Unknown archive entity.")
	}, JsonOptions);

	private static void Unique<T>(IEnumerable<T> rows)
	{
		var keys = new HashSet<string>(StringComparer.Ordinal);
		Require(rows.All(row => keys.Add(ArchiveKey(row))), "Duplicate taxonomy archive key.");
	}
	private static void ValidateUniqueKeys(State s)
	{
		Unique(s.Identities); Unique(s.Heads); Unique(s.Revisions); Unique(s.Nodes); Unique(s.Operations);
		Unique(s.Assignments); Unique(s.Current); Unique(s.ClassificationOperations); Unique(s.Migrations);
		Unique(s.Crosswalks); Unique(s.MigrationItems);
	}
	private static bool Equal<T>(T left, T right) => JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);

	private static void ValidateMigrationEvidence(State s, Dictionary<Guid, TaxonomyRevisionEntity> revisions,
		Dictionary<Guid, PurchaseClassificationEntity> assignments)
	{
		Require(s.Migrations.Select(m => (m.Actor, m.OperationId)).Distinct().Count() == s.Migrations.Count,
			"Ambiguous migration operation scope.");
		var migrations = s.Migrations.ToDictionary(m => (m.Actor, m.OperationId));
		foreach (var m in s.Migrations)
		{
			Require(m.OperationKind == LegacyTaxonomyMigrationService.InitialOperationKind && Text(m.SourceDataset)
				&& Text(m.Actor) && m.OperationId != Guid.Empty && Hash(m.RequestHash) && Utc(m.ObservedAt)
				&& revisions.TryGetValue(m.RevisionId, out _), "Invalid migration metadata.");
			var plan = DeserializeStrict<LegacyMigrationPlan>(m.PlanJson)
				?? throw new InvalidDataException("Null migration plan.");
			Require(plan.SourceDataset == m.SourceDataset && plan.OperationId == m.OperationId
				&& plan.OperationKind == m.OperationKind && plan.Actor == m.Actor && Utc(plan.ObservedAt)
				&& StoredMigrationObservedAt(plan.ObservedAt) == m.ObservedAt
				&& plan.RequestHash == m.RequestHash && plan.RevisionId == m.RevisionId && plan.CanCommit,
				"Migration plan differs from metadata.");
			// Recompute the deterministic result from its immutable source evidence, rather than trusting counts or outcomes.
			Require(plan.Crosswalks.All(c => c.Source == LegacyTaxonomySource.Category ? c.Category != null && c.Subcategory == null
				: c.Source == LegacyTaxonomySource.Subcategory && c.Subcategory != null && c.Category == null), "Invalid crosswalk source.");
			var source = new LegacyMigrationRequest(plan.SourceDataset, plan.OperationId, plan.OperationKind, plan.Actor, plan.ObservedAt,
				plan.Crosswalks.Where(c => c.Source == LegacyTaxonomySource.Category).Select(c => c.Category!).ToList(),
				plan.Crosswalks.Where(c => c.Source == LegacyTaxonomySource.Subcategory).Select(c => c.Subcategory!).ToList(),
				plan.Items.Select(i => i.Evidence).ToList());
			Require(Equal(plan, LegacyTaxonomyMigrationPlanner.Plan(source)), "Migration plan does not match frozen source.");
			var revision = revisions[m.RevisionId];
			Require(revision.Actor == m.Actor && revision.OperationId == m.OperationId && revision.RecordedAt == m.ObservedAt
				&& revision.AffectedCount == plan.Nodes.Count, "Migration revision differs from plan.");
			var nodes = s.Nodes.Where(n => n.RevisionId == m.RevisionId).OrderBy(n => n.CategoryId).ToList();
			var plannedNodes = plan.Nodes.Select(p => new TaxonomyNodeEntity
			{
				RevisionId = m.RevisionId,
				CategoryId = p.Node.Id,
				ParentId = p.Node.ParentId,
				Name = p.Node.Name,
				Definition = p.Node.Definition,
				Archived = p.Node.Archived
			}).OrderBy(n => n.CategoryId).ToList();
			Require(Equal(nodes, plannedNodes), "Migration snapshot differs from plan.");
			var crosswalks = s.Crosswalks.Where(c => c.Actor == m.Actor && c.OperationId == m.OperationId).ToList();
			// ValidateUniqueKeys makes (Actor, OperationId, ReceiptItemId) unique, so the item id keys one outcome.
			var outcomes = s.MigrationItems.Where(i => i.Actor == m.Actor && i.OperationId == m.OperationId).ToDictionary(i => i.ReceiptItemId);
			Require(crosswalks.Count == plan.Crosswalks.Count && outcomes.Count == plan.Items.Count, "Missing migration evidence.");
			foreach (var c in plan.Crosswalks)
			{
				var row = crosswalks.SingleOrDefault(r => r.SourceTable == c.Source.ToString() && r.SourceId == c.SourceId);
				Require(row != null && row.TargetId == c.TargetId
					&& Equal(DeserializeStrict<LegacyTaxonomyCrosswalk>(row.EvidenceJson), c), "Crosswalk differs from plan.");
			}
			foreach (var item in plan.Items)
			{
				var row = outcomes.GetValueOrDefault(item.Evidence.Id);
				Require(row != null && row.AssignmentId == item.AssignmentId && row.Resolution == item.Kind.ToString()
					&& Equal(DeserializeStrict<LegacyItemResolution>(row.EvidenceJson), item), "Migration outcome differs from plan.");
				Require(assignments.TryGetValue(item.AssignmentId, out var a) && a.ReceiptItemId == item.Evidence.Id && a.Sequence == 1
					&& a.CategoryId == item.CategoryId && a.TaxonomyRevisionId == m.RevisionId && a.RecordedAt == m.ObservedAt
					&& a.Actor == m.Actor && a.OperationId == m.OperationId && a.Source == "legacy_import"
					&& a.Reason == (item.Exception ?? item.Kind.ToString()), "Migration assignment differs from plan.");
			}
		}
		Require(s.Crosswalks.All(c => migrations.ContainsKey((c.Actor, c.OperationId)))
			&& s.MigrationItems.All(i => migrations.ContainsKey((i.Actor, i.OperationId))), "Orphan migration evidence.");
		Require(s.Revisions.Where(r => r.Sequence > 0).All(r => s.Operations.Any(o => o.Actor == r.Actor && o.OperationId == r.OperationId
			&& o.RevisionId == r.Id && o.AffectedCount == r.AffectedCount) || s.Migrations.Any(m => m.RevisionId == r.Id)), "Revision lacks its authoritative operation.");
		var authorized = s.ClassificationOperations.Select(o => o.AssignmentId)
			.Concat(s.MigrationItems.Select(i => i.AssignmentId)).ToHashSet();
		Require(s.Assignments.All(a => authorized.Contains(a.Id) || a.Source == "legacy_write"), "Assignment lacks its authoritative operation.");
	}

	// The frozen plan (and its request hash) retains every .NET tick. Its relational
	// timestamps use Npgsql's exact microsecond projection relative to PostgreSQL's
	// epoch, with integer division toward zero. Do not normalize immutable evidence
	// or accept a tolerance window: metadata must equal this one storage value.
	private static DateTimeOffset StoredMigrationObservedAt(DateTimeOffset observedAt)
	{
		// Npgsql's default infinity conversion preserves the maximum-value sentinel.
		if (observedAt == DateTimeOffset.MaxValue) { return observedAt; }
		long epochTicks = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
		long ticks = (observedAt.Ticks - epochTicks) / TimeSpan.TicksPerMicrosecond * TimeSpan.TicksPerMicrosecond + epochTicks;
		return new DateTimeOffset(ticks, TimeSpan.Zero);
	}
	internal static async Task<List<DeletionState>> ReadDeletionStateAsync(SqliteConnection sqlite, State taxonomy, CancellationToken ct)
	{
		string[] tables = ["categories", "subcategories", "item_templates", "receipts", "receipt_items", "transactions",
			"adjustments", "ynab_sync_records", "accepted_duplicate_pairs"];
		var ids = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
		foreach (string table in tables)
		{
			await using var command = sqlite.CreateCommand();
			command.CommandText = $"SELECT id FROM {table}";
			await using var reader = await command.ExecuteReaderAsync(ct);
			HashSet<Guid> values = [];
			while (await reader.ReadAsync(ct))
			{ Require(Guid.TryParse(reader.GetString(0), out var id) && id != Guid.Empty && values.Add(id), "Invalid portable business key."); }
			ids.Add(table, values);
		}
		List<DeletionState> result = [];
		var seen = new HashSet<(string, Guid)>();
		await using (var command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT kind,id,payload FROM portable_deletion_state";
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				var row = DeserializeStrict<DeletionState>(reader.GetString(2))
					?? throw new InvalidDataException("Null deletion record.");
				Require(row.Table == reader.GetString(0) && row.Id.ToString() == reader.GetString(1)
					&& ids.TryGetValue(row.Table, out var included) && included.Contains(row.Id)
					&& seen.Add((row.Table, row.Id)), "Invalid deletion record key or reference.");
				Require(row.DeletedAt is null
					? row.DeletedByUserId is null && row.DeletedByApiKeyId is null && row.CascadeDeletedByParentId is null
					: Utc(row.DeletedAt.Value), "Invalid deletion metadata.");
				result.Add(row);
			}
		}
		Require(result.Count == ids.Values.Sum(v => v.Count), "Missing portable deletion state.");
		Require(taxonomy.Assignments.All(a => ids["receipt_items"].Contains(a.ReceiptItemId)), "Classification references missing receipt item.");
		var dependencies = new (string Child, string Column, string Parent)[] {
			("subcategories", "category_id", "categories"), ("receipt_items", "receipt_id", "receipts"),
			("transactions", "receipt_id", "receipts"), ("transactions", "card_id", "cards"),
			("cards", "account_id", "accounts"), ("adjustments", "receipt_id", "receipts"),
			("ynab_sync_records", "local_transaction_id", "transactions"),
			("ynab_account_mappings", "receipts_account_id", "accounts"),
			("accepted_duplicate_pairs", "receipt_id_a", "receipts"), ("accepted_duplicate_pairs", "receipt_id_b", "receipts"),
			("receipt_items", "normalized_description_id", "normalized_descriptions"),
			("item_templates", "normalized_description_id", "normalized_descriptions"),
			("normalized_descriptions", "nearest_neighbour_id", "normalized_descriptions") };
		foreach (var (child, column, parent) in dependencies)
		{
			await using var command = sqlite.CreateCommand();
			command.CommandText = $"SELECT COUNT(*) FROM {child} c LEFT JOIN {parent} p ON p.id=c.{column} WHERE c.{column} IS NOT NULL AND p.id IS NULL";
			Require(Convert.ToInt64(await command.ExecuteScalarAsync(ct)) == 0, "Portable business dependency is missing.");
		}
		foreach (var row in result.Where(r => r.CascadeDeletedByParentId != null))
		{
			var parent = row.Table switch
			{
				"subcategories" => ("categories", "category_id"),
				"receipt_items" or "transactions" or "adjustments" => ("receipts", "receipt_id"),
				"ynab_sync_records" => ("transactions", "local_transaction_id"),
				_ => throw new InvalidDataException("Unsupported cascade deletion parent.")
			};
			await using var command = sqlite.CreateCommand();
			command.CommandText = $"SELECT {parent.Item2} FROM {row.Table} WHERE id=$id";
			command.Parameters.AddWithValue("$id", row.Id.ToString());
			Require(Guid.TryParse(Convert.ToString(await command.ExecuteScalarAsync(ct)), out var actual)
				&& actual == row.CascadeDeletedByParentId && result.Any(p => p.Table == parent.Item1 && p.Id == actual && p.DeletedAt != null),
				"Cascade deletion parent differs from dependency or is active.");
		}
		return result;
	}

}
