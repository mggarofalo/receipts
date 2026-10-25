using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Taxonomy;
using Infrastructure.Configurations;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

// v10's authoritative history. PostgreSQL's local WriteTransaction stamp is a shadow
// property and is deliberately absent. Restore must append revision+snapshot together.
internal static partial class PortableTaxonomyArchive
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{ UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };

	internal sealed record DeletionState(string Table, Guid Id, DateTimeOffset? DeletedAt,
		string? DeletedByUserId, Guid? DeletedByApiKeyId, Guid? CascadeDeletedByParentId);

	internal sealed record State(
		List<TaxonomyIdentityEntity> Identities, List<TaxonomyHeadEntity> Heads,
		List<TaxonomyRevisionEntity> Revisions, List<TaxonomyNodeEntity> Nodes,
		List<TaxonomyOperationEntity> Operations, List<PurchaseClassificationEntity> Assignments,
		List<CurrentClassificationEntity> Current, List<ClassificationOperationEntity> ClassificationOperations,
		List<LegacyMigrationEntity> Migrations, List<LegacyMigrationCrosswalkEntity> Crosswalks,
		List<LegacyMigrationItemEntity> MigrationItems);

	internal static async Task ExportAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken ct)
	{
		await using (var schema = sqlite.CreateCommand())
		{
			schema.CommandText = """
                CREATE TABLE taxonomy_archive(kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(kind,id));
                CREATE TABLE portable_deletion_state(kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(kind,id));
                """;
			await schema.ExecuteNonQueryAsync(ct);
		}
		async Task Write<T>(string table, string kind, string id, T value)
		{
			await using var command = sqlite.CreateCommand();
			command.CommandText = $"INSERT INTO {table}(kind,id,payload) VALUES($kind,$id,$payload)";
			command.Parameters.AddWithValue("$kind", kind);
			command.Parameters.AddWithValue("$id", id);
			command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value, JsonOptions));
			await command.ExecuteNonQueryAsync(ct);
		}
		async Task Rows<T>(string kind, IQueryable<T> query, Func<T, object> key) where T : class
		{
			await foreach (T row in query.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
			{ await Write("taxonomy_archive", kind, JsonSerializer.Serialize(key(row), JsonOptions), row); }
		}
		await Rows("identity", source.TaxonomyIdentities, r => r.Id);
		await Rows("head", source.TaxonomyHeads, r => r.Id);
		await Rows("revision", source.TaxonomyRevisions, r => r.Id);
		await Rows("node", source.TaxonomyNodes, r => new { r.RevisionId, r.CategoryId });
		await Rows("operation", source.TaxonomyOperations, r => new { r.Actor, r.OperationId });
		await Rows("assignment", source.Classifications, r => r.Id);
		await Rows("current", source.CurrentClassifications, r => r.ReceiptItemId);
		await Rows("classification_operation", source.ClassificationOperations, r => new { r.Actor, r.OperationId });
		await Rows("migration", source.LegacyMigrations, r => new { r.Actor, r.OperationId, r.OperationKind });
		await Rows("crosswalk", source.LegacyMigrationCrosswalks, r => new { r.Actor, r.OperationId, r.SourceTable, r.SourceId });
		await Rows("migration_item", source.LegacyMigrationItems, r => new { r.Actor, r.OperationId, r.ReceiptItemId });

		async Task Deletions<T>(string table, IQueryable<T> rows, Func<T, Guid> key) where T : class, ISoftDeletable
		{
			await foreach (T row in rows.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
			{
				Guid id = key(row);
				await Write("portable_deletion_state", table, id.ToString(), new DeletionState(table, id,
					row.DeletedAt, row.DeletedByUserId, row.DeletedByApiKeyId, row.CascadeDeletedByParentId));
			}
		}
		await Deletions("categories", source.Categories, r => r.Id);
		await Deletions("subcategories", source.Subcategories, r => r.Id);
		await Deletions("item_templates", source.ItemTemplates, r => r.Id);
		await Deletions("receipts", source.Receipts, r => r.Id);
		await Deletions("receipt_items", source.ReceiptItems, r => r.Id);
		await Deletions("transactions", source.Transactions, r => r.Id);
		await Deletions("adjustments", source.Adjustments, r => r.Id);
		await Deletions("ynab_sync_records", source.YnabSyncRecords, r => r.Id);
		await Deletions("accepted_duplicate_pairs", source.AcceptedDuplicatePairs, r => r.Id);
	}

	internal static async Task<State> ReadAsync(SqliteConnection sqlite, CancellationToken ct)
	{
		Dictionary<string, List<(string Id, string Payload)>> records = [];
		await using (var command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT kind,id,payload FROM taxonomy_archive ORDER BY kind,id";
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				string kind = reader.GetString(0);
				if (!records.TryGetValue(kind, out var rows)) { records.Add(kind, rows = []); }
				rows.Add((reader.GetString(1), reader.GetString(2)));
			}
		}
		List<T> Read<T>(string kind)
		{
			if (!records.Remove(kind, out var rows)) { return []; }
			return rows.Select(row =>
			{
				T value = DeserializeStrict<T>(row.Payload)
					?? throw new InvalidDataException("Null taxonomy record.");
				Require(row.Id == ArchiveKey(value), "Taxonomy archive key differs from payload.");
				return value;
			}).ToList();
		}
		State state = new(Read<TaxonomyIdentityEntity>("identity"), Read<TaxonomyHeadEntity>("head"),
			Read<TaxonomyRevisionEntity>("revision"), Read<TaxonomyNodeEntity>("node"),
			Read<TaxonomyOperationEntity>("operation"), Read<PurchaseClassificationEntity>("assignment"),
			Read<CurrentClassificationEntity>("current"), Read<ClassificationOperationEntity>("classification_operation"),
			Read<LegacyMigrationEntity>("migration"), Read<LegacyMigrationCrosswalkEntity>("crosswalk"),
			Read<LegacyMigrationItemEntity>("migration_item"));
		Require(records.Count == 0, "Unknown taxonomy archive record kind.");
		Validate(state);
		return state;
	}

	internal static void Validate(State state)
	{
		ValidateUniqueKeys(state);
		var revisions = state.Revisions.ToDictionary(r => r.Id);
		var identities = state.Identities.Select(r => r.Id).ToHashSet();
		Require(identities.Count == state.Identities.Count && !identities.Contains(Guid.Empty), "Invalid taxonomy identities.");
		Require(revisions.TryGetValue(TaxonomyConfiguration.GenesisId, out var genesis)
			&& genesis.Sequence == 0 && genesis.PreviousRevisionId is null && genesis.Actor == "system"
			&& genesis.OperationId == TaxonomyConfiguration.GenesisId && genesis.AffectedCount == 0
			&& genesis.RecordedAt == new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero)
			&& genesis.Reason == "Empty taxonomy foundation; legacy import is separate.", "Invalid taxonomy genesis.");
		var ordered = state.Revisions.OrderBy(r => r.Sequence).ToList();
		for (int i = 0; i < ordered.Count; i++)
		{
			var revision = ordered[i];
			Require(revision.Id != Guid.Empty && revision.OperationId != Guid.Empty
				&& Text(revision.Actor) && Text(revision.Reason) && revision.AffectedCount >= 0
				&& Utc(revision.RecordedAt), "Invalid revision metadata.");
			Require(revision.Sequence == i && (i == 0 || revision.PreviousRevisionId == ordered[i - 1].Id), "Broken taxonomy revision chain.");
			var nodes = state.Nodes.Where(n => n.RevisionId == revision.Id).ToList();
			Require(i != 0 || nodes.Count == 0, "Genesis snapshot must be empty.");
			Require(nodes.All(n => identities.Contains(n.CategoryId)), "Snapshot references unknown identity.");
			try { TaxonomyTree.Apply(nodes.Select(n => new TaxonomyNode(n.CategoryId, n.ParentId, n.Name, n.Definition, n.Archived)).ToList(), []); }
			catch (TaxonomyValidationException exception) { throw new InvalidDataException("Invalid taxonomy snapshot.", exception); }
			if (i > 0)
			{
				var priorIds = state.Nodes.Where(n => n.RevisionId == ordered[i - 1].Id).Select(n => n.CategoryId);
				Require(priorIds.All(id => nodes.Any(n => n.CategoryId == id)), "Snapshot dropped a known identity.");
				Require(revision.RecordedAt >= ordered[i - 1].RecordedAt, "Revision timestamps moved backwards.");
			}
		}
		Require(state.Nodes.All(n => revisions.ContainsKey(n.RevisionId)), "Unknown snapshot revision.");
		Require(state.Heads.Count == 1 && state.Heads[0].Id == 1 && state.Heads[0].RevisionId == ordered[^1].Id, "Invalid captured taxonomy head.");
		Require(state.Operations.Select(o => (o.Actor, o.OperationId)).Distinct().Count() == state.Operations.Count, "Duplicate taxonomy operation.");
		foreach (var operation in state.Operations)
		{
			Require(revisions.TryGetValue(operation.RevisionId, out var revision)
				&& Text(operation.Actor) && Text(operation.Reason) && operation.OperationId != Guid.Empty
				&& Utc(operation.RecordedAt) && operation.RecordedAt >= revision.RecordedAt
				&& operation.AffectedCount >= 0 && Hash(operation.RequestHash) && (operation.AffectedCount == 0 ||
					(revision.Actor == operation.Actor && revision.OperationId == operation.OperationId
					&& revision.RecordedAt == operation.RecordedAt && revision.Reason == operation.Reason
					&& revision.AffectedCount == operation.AffectedCount)), "Invalid taxonomy operation result.");
		}
		var assignments = state.Assignments.ToDictionary(a => a.Id);
		foreach (var group in state.Assignments.GroupBy(a => a.ReceiptItemId))
		{
			var history = group.OrderBy(a => a.Sequence).ToList();
			for (int i = 0; i < history.Count; i++)
			{
				var assignment = history[i];
				ValidateLegacyWriteEvidence(state, assignment);
				Require(assignment.Id != Guid.Empty && assignment.ReceiptItemId != Guid.Empty
					&& assignment.OperationId != Guid.Empty && Text(assignment.Actor) && Text(assignment.Source)
					&& Text(assignment.Reason) && Utc(assignment.RecordedAt)
					&& (i == 0 || assignment.RecordedAt >= history[i - 1].RecordedAt)
					&& assignment.Sequence == i + 1 && assignment.PreviousAssignmentId == (i == 0 ? null : history[i - 1].Id)
					&& revisions.ContainsKey(assignment.TaxonomyRevisionId)
					&& (assignment.CategoryId is null || state.Nodes.Any(n => n.RevisionId == assignment.TaxonomyRevisionId && n.CategoryId == assignment.CategoryId)), "Invalid classification history.");
			}
			Require(state.Current.Count(c => c.ReceiptItemId == group.Key && c.AssignmentId == history[^1].Id) == 1, "Current classification does not match history.");
		}
		Require(state.Current.Count == state.Assignments.Select(a => a.ReceiptItemId).Distinct().Count(), "Extra current classification.");
		foreach (var operation in state.ClassificationOperations)
		{ Require(assignments.TryGetValue(operation.AssignmentId, out var a) && a.Actor == operation.Actor && a.OperationId == operation.OperationId && Hash(operation.RequestHash), "Invalid classification operation."); }
		ValidateMigrationEvidence(state, revisions, assignments);

	}

	private static bool Hash(string? hash) => hash is not null && hash.Length == 64 && hash.All(Uri.IsHexDigit);
	private static void Require(bool condition, string message)
	{ if (!condition) { throw new InvalidDataException(message); } }
}
