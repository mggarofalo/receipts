using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Taxonomy;
using Infrastructure.Configurations;
using Infrastructure.Entities.CategoryAssistance;
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
		List<LegacyMigrationItemEntity> MigrationItems)
	{
		internal List<SourceRevision> SourceRevisions { get; init; } = [];
		internal bool IncludesReviewPreferences { get; init; }
		internal List<CategoryAssistanceReviewPreferenceEntity> ReviewPreferences { get; init; } = [];
		internal List<CategoryAssistanceReviewOperationEntity> ReviewOperations { get; init; } = [];
		internal bool IncludesReviewDispositions { get; init; }
		internal List<HistoricalGroupDispositionEntity> ReviewDispositions { get; init; } = [];
		internal List<HistoricalDispositionOperationEntity> ReviewDispositionOperations { get; init; } = [];
		internal bool IncludesHistorical { get; init; }
		internal List<HistoricalOperationEntity> HistoricalOperations { get; init; } = [];
		internal List<HistoricalOperationItemEntity> HistoricalItems { get; init; } = [];
		internal bool IncludesMappings { get; init; }
		internal List<CategoryMappingRevisionEntity> MappingRevisions { get; init; } = [];
		internal List<CategoryMappingHeadEntity> MappingHeads { get; init; } = [];
	}

	internal static async Task ExportAsync(ApplicationDbContext source, SqliteConnection sqlite, CancellationToken ct, bool candidate = false)
	{
		await using (var schema = sqlite.CreateCommand())
		{
			schema.CommandText = """
                CREATE TABLE historical_operation_archive(kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(kind,id));
                CREATE TABLE category_mapping_archive(kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(kind,id));
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
		await ExportMappingsAsync(source, sqlite, ct);
		if (!candidate) { await ExportHistoricalAsync(source, sqlite, ct); }
		await ExportSourceRevisionsAsync(source, sqlite, ct);
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
		if (candidate)
		{
			var state = await ReadAsync(sqlite, ct);
			state = state with
			{
				// Automatic Jev activity is deployment-local, like the assistance attempts it came from; its
				// classification history (source `jev`, evidence reason) is exported with every other assignment.
				HistoricalOperations = await PortableHistoricalOperations(source).ToListAsync(ct),
				HistoricalItems = await PortableHistoricalItems(source).ToListAsync(ct),
				IncludesReviewPreferences = true,
				ReviewPreferences = await source.CategoryAssistanceReviewPreferences.AsNoTracking().ToListAsync(ct),
				ReviewOperations = await source.CategoryAssistanceReviewOperations.AsNoTracking().ToListAsync(ct),
				IncludesReviewDispositions = true,
				ReviewDispositions = await source.HistoricalGroupDispositions.AsNoTracking().ToListAsync(ct),
				ReviewDispositionOperations = await source.HistoricalDispositionOperations.AsNoTracking().ToListAsync(ct)
			};
			ValidateCandidate(state);
			await WriteHistoricalCandidateAsync(sqlite, state, ct);
			await WriteReviewDispositionsAsync(sqlite, state, ct);
			await WriteReviewPreferencesAsync(sqlite, state, ct);
		}
	}

	internal static async Task<State> ReadAsync(SqliteConnection sqlite, CancellationToken ct, int version = PortableBackupFormat.CurrentVersion)
				=> await ReadCoreAsync(sqlite, ct, version, candidate: false);

	// v14 capture and restore share this codec.
	internal static Task<State> ReadCandidateAsync(SqliteConnection sqlite, CancellationToken ct)
			=> ReadCoreAsync(sqlite, ct, PortableAutomaticHistoricalV14.Version, candidate: true);

	private static async Task<State> ReadCoreAsync(SqliteConnection sqlite, CancellationToken ct, int version, bool candidate)
	{
		// Deserialize each kind while streaming its rows, so a large archive never holds
		// every raw payload string and its parsed entity at the same time.
		HashSet<string> kinds = [];
		await using (var command = sqlite.CreateCommand())
		{
			command.CommandText = "SELECT DISTINCT kind FROM taxonomy_archive";
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct)) { kinds.Add(reader.GetString(0)); }
		}
		async Task<List<T>> Read<T>(string kind)
		{
			List<T> values = [];
			if (!kinds.Remove(kind)) { return values; }
			await using var command = sqlite.CreateCommand();
			command.CommandText = "SELECT id,payload FROM taxonomy_archive WHERE kind=$kind ORDER BY id";
			command.Parameters.AddWithValue("$kind", kind);
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				T value = DeserializeStrict<T>(reader.GetString(1))
					?? throw new InvalidDataException("Null taxonomy record.");
				Require(reader.GetString(0) == ArchiveKey(value), "Taxonomy archive key differs from payload.");
				values.Add(value);
			}
			return values;
		}
		State state = new(await Read<TaxonomyIdentityEntity>("identity"), await Read<TaxonomyHeadEntity>("head"),
			await Read<TaxonomyRevisionEntity>("revision"), await Read<TaxonomyNodeEntity>("node"),
			await Read<TaxonomyOperationEntity>("operation"), await Read<PurchaseClassificationEntity>("assignment"),
			await Read<CurrentClassificationEntity>("current"), await Read<ClassificationOperationEntity>("classification_operation"),
			await Read<LegacyMigrationEntity>("migration"), await Read<LegacyMigrationCrosswalkEntity>("crosswalk"),
			await Read<LegacyMigrationItemEntity>("migration_item"));
		Require(kinds.Count == 0, "Unknown taxonomy archive record kind.");
		if (version >= 11) { state = await ReadMappingsAsync(sqlite, state, ct); }
		if (version >= 12)
		{
			state = candidate ? await ReadHistoricalCandidateAsync(sqlite, state, ct) : await ReadHistoricalAsync(sqlite, state, ct);
			state = await ReadSourceRevisionsAsync(sqlite, state, ct);
		}
		if (candidate) { state = await ReadReviewDispositionsAsync(sqlite, state, ct); state = await ReadReviewPreferencesAsync(sqlite, state, ct); }
		Validate(state, candidate);
		if (candidate) { await ValidateCandidateBusinessReferencesAsync(sqlite, state, ct); }
		return state;
	}

	internal static void Validate(State state) => Validate(state, allowAutomatic: false);

	internal static void ValidateCandidate(State state) => Validate(state, allowAutomatic: true);

	private static void Validate(State state, bool allowAutomatic)
	{
		ValidateUniqueKeys(state);
		ValidateMappings(state);
		if (allowAutomatic) { ValidateHistoricalCandidate(state); ValidateReviewDispositions(state); ValidateReviewPreferences(state); }
		else { ValidateHistorical(state); }
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
		// Keys are unique (ValidateUniqueKeys); lookups keep validation linear in archive size.
		var current = state.Current.ToDictionary(c => c.ReceiptItemId);
		var snapshotNodes = state.Nodes.Select(n => (n.RevisionId, n.CategoryId)).ToHashSet();
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
					&& (assignment.CategoryId is null || snapshotNodes.Contains((assignment.TaxonomyRevisionId, assignment.CategoryId.Value))), "Invalid classification history.");
			}
			Require(current.TryGetValue(group.Key, out var tip) && tip.AssignmentId == history[^1].Id, "Current classification does not match history.");
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
