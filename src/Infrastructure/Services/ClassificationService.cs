using System.Security.Cryptography;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Normalization;
using Application.Taxonomy;
using Domain.Taxonomy;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public sealed class ClassificationService(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock, Microsoft.Extensions.Options.IOptions<ClassificationOptions> options, ICommittedChangePublisher? publisher = null) : IClassificationService
{
	private const string WritesDisabledMessage = "Classification writes are disabled by server configuration. After verifying migration and backup, enable Classification__WritesEnabled and restart the API.";

	public async Task<PurchaseClassification> AssignAsync(string actor, Guid itemId, ClassificationCommand command, CancellationToken ct = default)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		await using var transaction = await db.Database.BeginTransactionAsync(ct);
		await Infrastructure.Utilities.NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		bool changed = false;
		PurchaseClassification result = await AssignInTransactionAsync(db, actor, itemId, command, options.Value.WritesEnabled, clock, ct, onChanged: () => changed = true);
		await transaction.CommitAsync(ct);
		if (changed && publisher != null)
		{
			await publisher.PublishAsync(new(CommittedEntityType.ReceiptItem, CommittedChangeType.Updated, itemId));
		}
		return result;
	}

	internal static async Task<PurchaseClassification> AssignInTransactionAsync(ApplicationDbContext db, string actor, Guid itemId,
		ClassificationCommand command, bool enabled, TimeProvider clock, CancellationToken ct, bool prepared = false, Action? onChanged = null)
		=> await AssignCoreAsync(db, actor, itemId, command, enabled, clock, ct, prepared, onChanged, null);

	// The caller owns the transaction and must atomically persist its historical outcome.
	// Frozen revisions, eligibility, suppression and progress remain the caller's responsibility.
	internal static Task<PurchaseClassification> AssignApprovedMappingInTransactionAsync(ApplicationDbContext db, string actor,
		Guid itemId, Guid categoryId, Guid taxonomyRevisionId, Guid? expectedAssignmentId, Guid operationId,
		CategoryMappingReuseEvidence evidence, bool enabled, TimeProvider clock, CancellationToken ct)
	{
		if (db.Database.CurrentTransaction == null)
		{ throw new InvalidOperationException("Approved mapping assignment requires a caller-owned transaction."); }
		if (evidence == null || evidence.MappingRevisionId == Guid.Empty || evidence.ApprovalTaxonomyRevisionId == Guid.Empty
			|| string.IsNullOrWhiteSpace(evidence.SourceFingerprint) || categoryId == Guid.Empty)
		{ throw new TaxonomyException(400, "Valid approved mapping evidence and category are required."); }
		ClassificationCommand command = new(categoryId, taxonomyRevisionId, expectedAssignmentId, operationId, JsonSerializer.Serialize(evidence));
		return AssignCoreAsync(db, actor, itemId, command, enabled, clock, ct, false, null, evidence);
	}

	private static async Task<PurchaseClassification> AssignCoreAsync(ApplicationDbContext db, string actor, Guid itemId,
		ClassificationCommand command, bool enabled, TimeProvider clock, CancellationToken ct, bool prepared,
		Action? onChanged, CategoryMappingReuseEvidence? mappingEvidence)
	{
		if (string.IsNullOrWhiteSpace(actor) || command.OperationId == Guid.Empty || command.TaxonomyRevisionId == Guid.Empty
			|| string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Contains('\0'))
		{ throw new TaxonomyException(400, "Actor, operation ID, taxonomy revision and reason are required."); }
		if (!enabled) { throw new TaxonomyException(409, WritesDisabledMessage); }
		TaxonomyHeadEntity head = await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE").AsNoTracking().SingleAsync(ct);
		if (!prepared) { await RequireReadyAsync(db, ct); }
		// Preserve the manual request hash and separate mapping operations from manual ones.
		string hash = mappingEvidence == null
			? Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { itemId, command })))
			: Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { itemId, command, mappingEvidence })));
		ClassificationOperationEntity? replay = await db.ClassificationOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Actor == actor && x.OperationId == command.OperationId, ct);
		if (replay != null)
		{
			if (replay.RequestHash != hash) { throw new TaxonomyException(409, "Operation ID was already used with a different request."); }
			return await ReadAsync(db, await db.Classifications.SingleAsync(x => x.Id == replay.AssignmentId, ct), ct);
		}
		if (head.RevisionId != command.TaxonomyRevisionId) { throw new TaxonomyException(409, "Taxonomy changed. Refresh before retrying."); }
		Guid parentId = await db.ReceiptItems.Where(x => x.Id == itemId).Select(x => x.ReceiptId).SingleOrDefaultAsync(ct);
		await ReceiptFactLock.AcquireAsync(db, new List<Guid> { parentId }, ct);
		ReceiptItemEntity? item = await db.ReceiptItems.FromSqlInterpolated($"SELECT * FROM receipts.\"ReceiptItems\" WHERE \"Id\" = {itemId} FOR UPDATE").IgnoreAutoIncludes().AsNoTracking().SingleOrDefaultAsync(ct);
		if (item == null || !await db.Receipts.AnyAsync(x => x.Id == item.ReceiptId, ct)) { throw new TaxonomyException(404, "Receipt item does not exist."); }
		// Do not flush unvalidated source changes or discard a caller's pending current assignment.
		db.ChangeTracker.DetectChanges();
		bool dirtyCurrent = db.ChangeTracker.Entries<CurrentClassificationEntity>().Any(entry => entry.Entity.ReceiptItemId == itemId
			&& entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
		bool dirtySource = mappingEvidence != null && (db.ChangeTracker.Entries<ReceiptItemEntity>().Any(entry => entry.Entity.Id == itemId
			&& entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
			|| db.ChangeTracker.Entries<ReceiptEntity>().Any(entry => entry.Entity.Id == item.ReceiptId
				&& entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted));
		if (dirtyCurrent || dirtySource)
		{ throw new TaxonomyException(409, "Pending changes must be saved or discarded before classification."); }
		CurrentClassificationEntity? current = await db.CurrentClassifications.SingleOrDefaultAsync(x => x.ReceiptItemId == itemId, ct);
		// A caller may already track an older current row; refresh it after taking the receipt lock.
		if (current != null) { await db.Entry(current).ReloadAsync(ct); }
		if (current?.AssignmentId != command.ExpectedAssignmentId) { throw new TaxonomyException(409, "Classification changed. Refresh before retrying."); }
		if (command.CategoryId is Guid category && !await db.TaxonomyNodes.AnyAsync(x => x.RevisionId == head.RevisionId && x.CategoryId == category && !x.Archived, ct))
		{ throw new TaxonomyException(400, "Category must be selectable in the expected taxonomy revision."); }
		if (mappingEvidence != null)
		{
			string location = await db.Receipts.Where(x => x.Id == item.ReceiptId).Select(x => x.Location).SingleAsync(ct);
			NormalizationSourceKey source = NormalizationCatalogPolicy.Normalize(new(location, item.Description, item.ReceiptItemCode));
			CategoryMappingResolution resolution = await CategoryMappingService.ResolveInTransactionAsync(db, source,
				mappingEvidence.SourceFingerprint, head.RevisionId, null, ct);
			if (resolution.Disposition != "approved" || resolution.CategoryId != command.CategoryId
				|| resolution.MappingRevisionId != mappingEvidence.MappingRevisionId
				|| resolution.ApprovalTaxonomyRevisionId != mappingEvidence.ApprovalTaxonomyRevisionId)
			{ throw new TaxonomyException(409, "Approved mapping evidence changed. Refresh before retrying."); }
		}
		PurchaseClassificationEntity assignment = await AppendAsync(db, actor, itemId, command.CategoryId, head.RevisionId,
			current, command.OperationId, mappingEvidence == null ? "manual" : "approved_mapping", command.Reason, clock, ct);
		db.ClassificationOperations.Add(new() { Actor = actor, OperationId = command.OperationId, RequestHash = hash, AssignmentId = assignment.Id });
		await db.SaveChangesAsync(ct);
		onChanged?.Invoke();
		return await ReadAsync(db, assignment, ct);
	}

	private static async Task<PurchaseClassificationEntity> AppendAsync(ApplicationDbContext db, string actor, Guid itemId,
		Guid? categoryId, Guid revisionId, CurrentClassificationEntity? current, Guid operationId, string source, string reason,
		TimeProvider clock, CancellationToken ct, string? legacyCategory = null, string? legacySubcategory = null, string? legacyResolution = null)
	{
		long sequence = current == null ? 1 : 1 + await db.Classifications.Where(x => x.Id == current.AssignmentId).Select(x => x.Sequence).SingleAsync(ct);
		// Persist and return the same PostgreSQL microsecond precision so retries reproduce the original result exactly.
		DateTimeOffset now = clock.GetUtcNow();
		now = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
		PurchaseClassificationEntity assignment = new()
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = itemId,
			Sequence = sequence,
			PreviousAssignmentId = current?.AssignmentId,
			CategoryId = categoryId,
			TaxonomyRevisionId = revisionId,
			RecordedAt = now,
			Actor = actor,
			Source = source,
			Reason = reason,
			OperationId = operationId,
			LegacyCategory = legacyCategory,
			LegacySubcategory = legacySubcategory,
			LegacyResolution = legacyResolution
		};
		db.Classifications.Add(assignment);
		if (current == null) { db.CurrentClassifications.Add(new() { ReceiptItemId = itemId, AssignmentId = assignment.Id }); }
		else { current.AssignmentId = assignment.Id; }
		// Every classification write appends here, so this is where envelopes follow the item type.
		await ItemEnvelopeWriter.AfterItemTypeChangeAsync(db, itemId, categoryId, revisionId, ct);
		return assignment;
	}

	internal static void RequireRequestId(IEnumerable<ReceiptItemEntity> items, Guid? requestId)
	{
		if (items.Any(x => x.Classification != null) && (requestId == null || requestId == Guid.Empty))
		{ throw new TaxonomyException(400, "Explicit aggregate classification requires a nonempty clientRequestId for atomic retries."); }
	}

	private sealed class AggregateState
	{
		public Guid? RevisionId { get; set; }
		public List<TaxonomyNodeEntity> Nodes { get; set; } = [];
		public HashSet<Guid> Changed { get; } = [];
		public HashSet<Guid> Existing { get; } = [];
		public bool MappingReady { get; set; }
	}
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ApplicationDbContext, AggregateState> Aggregates = new();

	private static async Task RequireReadyAsync(ApplicationDbContext db, CancellationToken ct)
	{
		if (!await db.LegacyMigrations.AnyAsync(x => x.OperationKind == LegacyTaxonomyMigrationService.InitialOperationKind, ct))
		{ throw new TaxonomyException(409, "Classification writes require a completed legacy taxonomy backfill."); }
		if (await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().AnyAsync(item =>
			!db.CurrentClassifications.Any(current => current.ReceiptItemId == item.Id), ct))
		{ throw new TaxonomyException(409, "Classification backfill is incomplete: existing receipt items have no assignment. Repair or restore the missing classification evidence before retrying."); }
	}

	internal static Task ProtectLegacyEditAsync(ApplicationDbContext db, ReceiptItemEntity incoming, ReceiptItemEntity stored, CancellationToken ct)
	{
		AggregateState state = Aggregates.GetOrCreateValue(db);
		state.Existing.Add(stored.Id);
		if (incoming.Classification == null && (incoming.Category != stored.Category || incoming.Subcategory != stored.Subcategory))
		{ state.Changed.Add(stored.Id); }
		return Task.CompletedTask;
	}

	internal static async Task PrepareAggregateAsync(ApplicationDbContext db, IEnumerable<ReceiptItemEntity> items, bool enabled, CancellationToken ct)
	{
		bool explicitWrite = items.Any(x => x.Classification != null);
		if (explicitWrite && !enabled)
		{ throw new TaxonomyException(409, WritesDisabledMessage); }
		bool migrated = await db.LegacyMigrations.AnyAsync(x => x.OperationKind == LegacyTaxonomyMigrationService.InitialOperationKind, ct);
		if (!explicitWrite && !migrated) { return; }
		TaxonomyHeadEntity head = db.Database.IsNpgsql()
			? await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE").SingleAsync(ct)
			: await db.TaxonomyHeads.SingleAsync(ct);
		if (explicitWrite) { await RequireReadyAsync(db, ct); }
		AggregateState state = Aggregates.GetOrCreateValue(db);
		// Check preexisting evidence before the new aggregate is persisted. Ordinary
		// saving retains its legacy fallback while an incomplete backfill is repaired.
		state.MappingReady = enabled && migrated && (explicitWrite || !await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().AnyAsync(item =>
			!db.CurrentClassifications.Any(current => current.ReceiptItemId == item.Id), ct));
		if (migrated)
		{
			state.RevisionId = head.RevisionId;
			state.Nodes = await db.TaxonomyNodes.AsNoTracking().Where(x => x.RevisionId == head.RevisionId).ToListAsync(ct);
		}
	}

	private static Guid? ResolveLegacy(AggregateState state, string? category, string? subcategory)
	{
		// Empty and dirty evidence remains distinct from a deliberate null choice.
		if (string.IsNullOrEmpty(category)) { return null; }
		var roots = state.Nodes.Where(x => x.ParentId == null && x.Name == category).ToList();
		if (roots.Count != 1 || roots[0].Archived) { return null; }
		if (subcategory == null) { return roots[0].CategoryId; }
		var children = state.Nodes.Where(x => x.ParentId == roots[0].CategoryId && x.Name == subcategory).ToList();
		return children.Count == 1 && !children[0].Archived ? children[0].CategoryId : null;
	}

	internal static async Task ApplyAggregateAsync(ApplicationDbContext db, IEnumerable<ReceiptItemEntity> items, bool enabled, CancellationToken ct)
	{
		AggregateState state = Aggregates.GetOrCreateValue(db);
		foreach (ReceiptItemEntity item in items.OrderBy(x => x.Id))
		{
			if (item.Classification is { } intent)
			{
				string actor = db.ReceiptWriteActor ?? throw new TaxonomyException(400, "A signed-in actor is required for classification.");
				PurchaseClassification result = await AssignInTransactionAsync(db, actor, item.Id,
					new(intent.CategoryId, intent.TaxonomyRevisionId, intent.ExpectedAssignmentId, intent.OperationId, intent.Reason), enabled, TimeProvider.System, ct, prepared: true);
				string category = result.Path.FirstOrDefault()?.Name ?? item.Category;
				string? subcategory = result.Path.Skip(1).FirstOrDefault()?.Name;
				if (intent.CategoryId != null && (item.Category != category || item.Subcategory != subcategory))
				{ throw new TaxonomyException(409, "Legacy category labels contradict the explicit classification path."); }
				continue;
			}
			if (state.RevisionId is not Guid revision) { continue; }
			CurrentClassificationEntity? current = await db.CurrentClassifications.SingleOrDefaultAsync(x => x.ReceiptItemId == item.Id, ct);
			if (state.Existing.Contains(item.Id) && !state.Changed.Contains(item.Id)) { continue; }
			// A replay or incidental edit must never replace an existing assignment, including clear.
			if (!state.Existing.Contains(item.Id) && current != null) { continue; }
			if (state.MappingReady && !state.Existing.Contains(item.Id) && string.IsNullOrEmpty(item.Category)
				&& item.Subcategory == null && await ApplyApprovedMappingAsync(db, item, revision, ct))
			{ continue; }
			Guid? target = ResolveLegacy(state, item.Category, item.Subcategory);
			if (state.Existing.Contains(item.Id) && target == null)
			{ throw new TaxonomyException(409, "Legacy category labels do not identify one selectable root or two-level path. Refresh and use an explicit classification choice."); }
			string legacyActor = db.ReceiptWriteActor ?? throw new TaxonomyException(400, "A signed-in actor is required for classification.");
			await AppendAsync(db, legacyActor, item.Id, target, revision, current, Guid.NewGuid(), "legacy_write",
				"Observed legacy category labels during receipt save.", TimeProvider.System, ct, item.Category, item.Subcategory,
				target == null ? "unresolved_path" : "resolved_path");
			await db.SaveChangesAsync(ct);
		}
	}

	private static async Task<bool> ApplyApprovedMappingAsync(ApplicationDbContext db, ReceiptItemEntity item,
		Guid revision, CancellationToken ct)
	{
		string location = await db.Receipts.Where(row => row.Id == item.ReceiptId).Select(row => row.Location).SingleAsync(ct);
		NormalizationSourceKey source = NormalizationCatalogPolicy.Normalize(new(location, item.Description, item.ReceiptItemCode));
		string fingerprint = NormalizationCatalogPolicy.Hash(source);
		CategoryMappingResolution resolution = await CategoryMappingService.ResolveInTransactionAsync(db,
			source, fingerprint, revision, null, ct);
		if (resolution.Disposition != "approved") { return false; }
		string actor = db.ReceiptWriteActor ?? throw new TaxonomyException(400, "A signed-in actor is required for classification.");
		// Stable for retries of this persisted item; separate from user supplied manual operation IDs.
		Guid operation = new(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
			new { Kind = "approved_category_mapping", item.Id, resolution.MappingRevisionId })).AsSpan(0, 16));
		string reason = JsonSerializer.Serialize(new CategoryMappingReuseEvidence(
			resolution.MappingRevisionId!.Value, resolution.ApprovalTaxonomyRevisionId!.Value, fingerprint));
		ClassificationCommand command = new(resolution.CategoryId, revision, null, operation, reason);
		string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { itemId = item.Id, command })));
		PurchaseClassificationEntity assignment = await AppendAsync(db, actor, item.Id, resolution.CategoryId,
			revision, null, operation, "approved_mapping", reason, TimeProvider.System, ct);
		db.ClassificationOperations.Add(new() { Actor = actor, OperationId = operation, RequestHash = hash, AssignmentId = assignment.Id });
		await db.SaveChangesAsync(ct);
		return true;
	}

	public async Task<ClassificationHistory> HistoryAsync(Guid itemId, long? beforeSequence, int limit, CancellationToken ct = default)
	{
		if (limit is < 1 or > 100 || beforeSequence is <= 0) { throw new TaxonomyException(400, "Limit must be 1–100 and cursor must be positive."); }
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		if (!await db.ReceiptItems.AnyAsync(x => x.Id == itemId && db.Receipts.Any(r => r.Id == x.ReceiptId), ct)) { throw new TaxonomyException(404, "Receipt item does not exist."); }
		List<PurchaseClassificationEntity> rows = await db.Classifications.AsNoTracking().Where(x => x.ReceiptItemId == itemId && (!beforeSequence.HasValue || x.Sequence < beforeSequence))
			.OrderByDescending(x => x.Sequence).Take(limit + 1).ToListAsync(ct);
		List<PurchaseClassification> items = [];
		foreach (PurchaseClassificationEntity row in rows.Take(limit)) { items.Add(await ReadAsync(db, row, ct)); }
		return new(items, rows.Count > limit ? items[^1].Sequence : null);
	}

	private static async Task<PurchaseClassification> ReadAsync(ApplicationDbContext db, PurchaseClassificationEntity row, CancellationToken ct)
	{
		List<TaxonomyNode> path = [];
		if (row.CategoryId is Guid category)
		{
			Dictionary<Guid, TaxonomyNodeEntity> nodes = await db.TaxonomyNodes.AsNoTracking().Where(x => x.RevisionId == row.TaxonomyRevisionId).ToDictionaryAsync(x => x.CategoryId, ct);
			Guid? cursor = category;
			while (cursor.HasValue) { TaxonomyNodeEntity node = nodes[cursor.Value]; path.Add(new(node.CategoryId, node.ParentId, node.Name, node.Definition, node.Archived)); cursor = node.ParentId; }
			path.Reverse();
		}
		return new(row.Id, row.ReceiptItemId, row.Sequence, row.PreviousAssignmentId, row.CategoryId, row.TaxonomyRevisionId,
			row.RecordedAt, row.Actor, row.Source, row.Reason, row.OperationId, path, row.LegacyCategory, row.LegacySubcategory, row.LegacyResolution);
	}
}
