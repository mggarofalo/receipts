using System.Data;
using System.Security.Cryptography;
using System.Text;
using Application.Interfaces.Services;
using Application.Models;
using Application.Models.CommittedChanges;
using Application.Models.NormalizedDescriptions;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using Infrastructure.Configurations;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pgvector;

namespace Infrastructure.Services;

public class NormalizedDescriptionService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	IEmbeddingService embeddingService,
	NormalizedDescriptionMapper mapper,
	ICommittedChangePublisher committedChangePublisher) : INormalizedDescriptionService
{
	// Direct-construction overload for isolated tests and tools that have no connected clients.
	// Dependency injection resolves the publisher-aware constructor in every configured host.
	public NormalizedDescriptionService(
		IDbContextFactory<ApplicationDbContext> contextFactory,
		IEmbeddingService embeddingService,
		NormalizedDescriptionMapper mapper)
		: this(contextFactory, embeddingService, mapper, new NullCommittedChangePublisher())
	{
	}

	public const string ReceiptItemNotFound = "Receipt item not found.";
	private const string PostgreSQL = "Npgsql.EntityFrameworkCore.PostgreSQL";

	// Matches the name CollectAuditEntries derives by stripping the "Entity" suffix, so explicit
	// entries land in the same EntityType bucket as the automatic ones (RECEIPTS-890).
	internal const string NormalizedDescriptionEntityType = "NormalizedDescription";

	// How many distinct raw receipt-item descriptions to surface per canonical row. Enough for a
	// reviewer to recognise what the entry actually covers without turning the queue into a dump
	// of every line item.
	internal const int MaxSampleRawDescriptions = 3;


	/// <summary>
	/// The canonical entry for a user-declared item template (RECEIPTS-881).
	/// </summary>
	/// <remarks>
	/// Records the identity metadata named by a user-created template. This does not establish
	/// receipt verification or a reusable scoped source mapping. Embeddings enrich candidate retrieval only.
	/// </remarks>
	public async Task<NormalizedDescription> GetOrCreateForTemplateAsync(string templateName, CancellationToken cancellationToken)
	{
		string normalized = (templateName ?? string.Empty).Trim();
		if (string.IsNullOrEmpty(normalized))
		{
			throw new ArgumentException(NormalizedDescription.CanonicalNameCannotBeEmpty, nameof(templateName));
		}

		using ApplicationDbContext context = contextFactory.CreateDbContext();

		NormalizedDescriptionEntity? existing = await FindExactCaseInsensitiveAsync(context, normalized, cancellationToken);
		if (existing is not null)
		{
			// A tombstone the user has now contradicted by hand. Their explicit, later declaration
			// wins over the earlier "this text is not worth an entry" — but it is a reversal of a
			// recorded decision, so it is audited rather than flipped silently. The alternative,
			// refusing the template, leaves the user unable to name their own item with no way to
			// discover why (RECEIPTS-876 tombstones are not surfaced anywhere they would look).
			if (existing.Status == NormalizedDescriptionStatus.Rejected)
			{
				existing.Status = NormalizedDescriptionStatus.Active;
				context.AddSemanticAuditEntry(
					NormalizedDescriptionEntityType,
					existing.Id.ToString(),
					AuditAction.Update,
					[
						new FieldChange { FieldName = "operation", OldValue = null, NewValue = "ReinstateForTemplate" },
						new FieldChange { FieldName = "status", OldValue = NormalizedDescriptionStatus.Rejected.ToString(), NewValue = NormalizedDescriptionStatus.Active.ToString() },
						new FieldChange { FieldName = "canonicalName", OldValue = null, NewValue = existing.CanonicalName },
					],
					DateTimeOffset.UtcNow);
				await context.SaveChangesAsync(cancellationToken);
				await committedChangePublisher.PublishAsync(new(
					CommittedEntityType.NormalizedDescription,
					CommittedChangeType.Updated,
					existing.Id));
			}
			// A PendingReview row is left pending-but-linked rather than auto-approved. The
			// template says what the item is called; it does not say the resolver's *grouping* of
			// whatever raw text landed there is right, and that grouping is what review is for.

			return mapper.ToDomain(existing);
		}

		Vector? embedding = null;
		if (embeddingService.IsConfigured)
		{
			float[]? data = await TryGenerateEmbeddingAsync(normalized, cancellationToken);
			embedding = data is { Length: > 0 } ? new Vector(data) : null;
		}

		(NormalizedDescriptionEntity created, _) = await InsertAsync(
			context,
			normalized,
			NormalizedDescriptionStatus.Active,
			embedding,
			cancellationToken);

		return mapper.ToDomain(created);
	}

	public async Task<NormalizedDescriptionDetail?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		DetailRow? row = await ProjectDetails(context)
			.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
		if (row is null)
		{
			return null;
		}

		await PopulateSampleRawDescriptionsAsync(context, [row], cancellationToken);
		return row.ToDetail();
	}

	public const int MaxPageSize = 200;

	/// <summary>
	/// One page of canonical rows, optionally filtered by status and search term (RECEIPTS-879).
	/// </summary>
	/// <remarks>
	/// Paginated in SQL. The registry previously loaded every Active row and filtered in the
	/// browser, and every merge-dialog open paid for the same full list — fine for a handful of
	/// rows, not for the thousands of distinct descriptions grocery receipts generate.
	///
	/// Search matches the display name and the matched text, so an entry is findable both by what
	/// it is called now and by the receipt text it still resolves on (RECEIPTS-876).
	///
	/// Ordered by display name with the id as a tiebreaker: the name is not unique across rows
	/// under a case-insensitive collation, and without a total order offset pagination can skip or
	/// repeat a row between page requests.
	/// </remarks>
	public async Task<PagedResult<NormalizedDescriptionDetail>> GetAllAsync(
		IReadOnlyCollection<NormalizedDescriptionStatus>? statuses,
		string? q,
		int offset,
		int limit,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<DetailRow> query = ProjectDetails(context);

		if (statuses is { Count: > 0 })
		{
			// Materialised to a List because EF translates Contains over a local collection to
			// an IN clause; the interface takes a read-only collection so callers cannot mutate
			// the filter mid-query.
			List<NormalizedDescriptionStatus> wanted = [.. statuses];
			query = wanted.Count == 1
				? query.Where(d => d.Status == wanted[0])
				: query.Where(d => wanted.Contains(d.Status));
		}

		string? trimmed = q?.Trim();
		if (!string.IsNullOrEmpty(trimmed))
		{
			string lowered = trimmed.ToLowerInvariant();
			query = query.Where(d =>
				(d.DisplayLabel != null && d.DisplayLabel.ToLower().Contains(lowered))
				|| d.CanonicalName.ToLower().Contains(lowered));
		}

		// Counted before paging, so the client can page through a filtered set rather than being
		// told how many rows exist in total and then handed a shorter list.
		int total = await query.CountAsync(cancellationToken);

		List<DetailRow> rows = await query
			.OrderBy(d => d.DisplayLabel ?? d.CanonicalName)
			.ThenBy(d => d.Id)
			.Skip(offset)
			.Take(limit)
			.ToListAsync(cancellationToken);
		await PopulateSampleRawDescriptionsAsync(context, rows, cancellationToken);

		return new PagedResult<NormalizedDescriptionDetail>(
			[.. rows.Select(r => r.ToDetail())],
			total,
			offset,
			limit);
	}

	public const string MergeIdsMustDiffer = "A description cannot be merged into itself.";

	/// <summary>
	/// Merges <paramref name="discardId"/> into <paramref name="keepId"/> and returns the number
	/// of live receipt items re-linked.
	/// </summary>
	/// <remarks>
	/// The return value means one thing only: how many live items moved. It used to mean three —
	/// "the ids were identical", "a row does not exist", and "a real merge re-linked nothing" all
	/// returned 0, and the controller answered 200 to all three. Merging against a stale id was
	/// therefore indistinguishable from success, and the registry list is routinely minutes old
	/// (RECEIPTS-891). The two caller errors now throw.
	/// </remarks>
	/// <exception cref="ArgumentException">The two ids are the same row.</exception>
	/// <exception cref="KeyNotFoundException">Either id does not exist.</exception>
	public virtual async Task<int> MergeAsync(Guid keepId, Guid discardId, CancellationToken cancellationToken)
	{
		if (keepId == discardId)
		{
			throw new ArgumentException(MergeIdsMustDiffer, nameof(discardId));
		}

		using ApplicationDbContext read = contextFactory.CreateDbContext();

		NormalizedDescriptionEntity? keep = await read.NormalizedDescriptions
			.FirstOrDefaultAsync(e => e.Id == keepId, cancellationToken);
		NormalizedDescriptionEntity? discard = await read.NormalizedDescriptions
			.FirstOrDefaultAsync(e => e.Id == discardId, cancellationToken);

		// Named individually rather than as one "not found": an admin who merged the wrong way
		// round needs to know which of the two ids was the stale one.
		if (keep is null)
		{
			throw new KeyNotFoundException($"Normalized description {keepId} not found.");
		}

		if (discard is null)
		{
			throw new KeyNotFoundException($"Normalized description {discardId} not found.");
		}

		Guid keepRevision = keep.NormalizationRevision;
		Guid discardRevision = discard.NormalizationRevision;

		using ApplicationDbContext context = contextFactory.CreateDbContext();
		await using IDbContextTransaction? transaction = context.Database.IsRelational()
			? await context.Database.BeginTransactionAsync(cancellationToken) : null;
		await NormalizationWriteGuard.LockTargetsAsync(context, [keepId, discardId], cancellationToken);
		keep = await context.NormalizedDescriptions.SingleOrDefaultAsync(value => value.Id == keepId, cancellationToken);
		discard = await context.NormalizedDescriptions.SingleOrDefaultAsync(value => value.Id == discardId, cancellationToken);
		if (keep is null || discard is null || keep.NormalizationRevision != keepRevision || discard.NormalizationRevision != discardRevision)
		{ throw new NormalizationCatalogException(409, "merge_target_changed", "A selected identity changed. Review the merge again."); }
		// Re-link EVERY ReceiptItem pointing at discard — including soft-deleted (trashed) ones,
		// hence IgnoreQueryFilters. The ReceiptItem -> NormalizedDescription FK is
		// DeleteBehavior.SetNull, so any row still pointing at discard when it is removed below
		// has its link silently nulled by the database; a trashed item would then come back from
		// the recycle bin unlinked, with no error raised anywhere. Same class of bug as the
		// soft-deleted-transaction stranding fixed in AccountMergeService (RECEIPTS-801).
		Guid[] currentIds = await context.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes()
			.Where(item => item.NormalizedDescriptionId == discardId).Select(item => item.Id).ToArrayAsync(cancellationToken);
		await NormalizationWriteGuard.LockItemsAsync(context, currentIds, cancellationToken);
		List<ReceiptItemEntity> items = await context.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes()
			.Where(item => currentIds.Contains(item.Id) && item.NormalizedDescriptionId == discardId).ToListAsync(cancellationToken);
		var parents = await context.Receipts.IgnoreQueryFilters().IgnoreAutoIncludes().Where(parent => items.Select(item => item.ReceiptId).Contains(parent.Id))
			.ToDictionaryAsync(parent => parent.Id, cancellationToken);
		var oldMappings = await context.NormalizationSourceMappings.Where(mapping => mapping.TargetId == discardId)
			.ToDictionaryAsync(mapping => mapping.Id, cancellationToken);
		HashSet<Guid> transferable = items.Where(item => parents.TryGetValue(item.ReceiptId, out var parent)
			&& ReceiptNormalizationPolicy.EvidenceIsCurrent(new(
				item.ReviewedSourceHash == NormalizationCatalogPolicy.Hash(new(parent.Location, item.Description, item.ReceiptItemCode)),
				item.NormalizationReviewDecisionId.HasValue && item.NormalizationReviewDecisionId == item.NormalizationAssignmentId,
				item.ReviewedTargetId == discardId, discard.Status == NormalizedDescriptionStatus.Active,
				item.NormalizationReviewReason == "purchase_review",
				item.NormalizationMappingId is Guid mappingId && oldMappings.TryGetValue(mappingId, out var mapping)
					&& mapping.State == "active" && mapping.Revision == item.ReviewMappingRevision,
				item.NormalizationReviewState))).Select(item => item.Id).ToHashSet();

		// Repoint, and clear the match score: it was measured against the discarded row, and no
		// decision or display reads a re-measured score any more (RECEIPTS-1099).
		int clearedMatchScoreCount = 0;
		foreach (ReceiptItemEntity item in items)
		{
			item.NormalizedDescriptionId = keepId;
			if (transferable.Contains(item.Id))
			{
				item.ReviewedTargetId = keepId;
				item.NormalizationAssignmentId = Guid.NewGuid();
				item.NormalizationReviewDecisionId = item.NormalizationAssignmentId;
			}
			else
			{
				var parent = parents[item.ReceiptId];
				item.NormalizationIntent = new(ReceiptNormalizationIntentKind.Defer, Guid.NewGuid(),
					NormalizationCatalogPolicy.Hash(new(parent.Location, item.Description, item.ReceiptItemCode)), ExpectedItemRevision: item.NormalizationRevision);
			}
			if (item.NormalizedDescriptionMatchScore is not null)
			{
				item.NormalizedDescriptionMatchScore = null;
				if (item.DeletedAt is null)
				{
					clearedMatchScoreCount++;
				}
			}
		}

		// Item templates that declared the discarded row follow it (RECEIPTS-881). Same reasoning
		// as the receipt items above: their FK is SetNull, so leaving them to the database would
		// silently strip the link and quietly put every item entered from that template back
		// through the resolver — a regression with nothing raised anywhere. IgnoreQueryFilters
		// so a soft-deleted template does not come back from the recycle bin unlinked.
		List<ItemTemplateEntity> templates = await context.ItemTemplates
			.IgnoreQueryFilters()
			.Where(t => t.NormalizedDescriptionId == discardId)
			.ToListAsync(cancellationToken);

		foreach (ItemTemplateEntity template in templates)
		{
			template.NormalizedDescriptionId = keepId;
		}

		context.NormalizedDescriptions.Remove(discard);
		foreach (var mapping in await context.NormalizationSourceMappings.Where(mapping => mapping.TargetId == discardId).ToListAsync(cancellationToken))
		{
			mapping.TargetId = keepId;
			mapping.Revision = Guid.NewGuid();
		}

		// A merge changes the identity approved by an immutable historical snapshot.
		// Keep the explicitly redirected reusable mapping, but require a fresh preview
		// before assigning purchases that were not yet processed.
		foreach (var operation in await context.NormalizationCatalogOperations.Where(operation => operation.TargetId == discardId
			&& (operation.State == "preview" || operation.State == "applying" || operation.State == "paused")).ToListAsync(cancellationToken))
		{
			operation.State = "cancelled";
			operation.Revision = Guid.NewGuid();
			operation.ErrorCode = "target_changed";
			operation.ErrorDetail = "The approved target was merged. The reusable mapping follows the merge. To process remaining purchases, revoke that mapping, then review and approve a fresh preview.";
		}

		int liveCount = items.Count(item => item.DeletedAt is null);
		int trashedCount = items.Count - liveCount;

		// Record the merge itself, not just its mechanical parts. Two entries, keyed to each side,
		// so the trail is findable whether you start from the row that survived or the one that
		// vanished — the discarded id is about to stop existing, and an entry filed under it is the
		// only way to answer "what happened to X?" afterwards.
		DateTimeOffset now = DateTimeOffset.UtcNow;
		string discardName = discard.CanonicalName;
		string keepName = keep.CanonicalName;

		context.AddSemanticAuditEntry(
			NormalizedDescriptionEntityType,
			keepId.ToString(),
			AuditAction.Merge,
			[
				new FieldChange { FieldName = "mergedFrom", OldValue = discardName, NewValue = keepName },
				new FieldChange { FieldName = "discardedId", OldValue = discardId.ToString(), NewValue = null },
				new FieldChange { FieldName = "relinkedItemCount", OldValue = null, NewValue = liveCount.ToString() },
				new FieldChange { FieldName = "relinkedTrashedItemCount", OldValue = null, NewValue = trashedCount.ToString() },
				new FieldChange { FieldName = "clearedMatchScoreCount", OldValue = null, NewValue = clearedMatchScoreCount.ToString() },
				new FieldChange { FieldName = "relinkedTemplateCount", OldValue = null, NewValue = templates.Count.ToString() },
			],
			now);

		context.AddSemanticAuditEntry(
			NormalizedDescriptionEntityType,
			discardId.ToString(),
			AuditAction.Merge,
			[
				new FieldChange { FieldName = "mergedInto", OldValue = discardName, NewValue = keepName },
				new FieldChange { FieldName = "keptId", OldValue = null, NewValue = keepId.ToString() },
				new FieldChange { FieldName = "relinkedItemCount", OldValue = null, NewValue = liveCount.ToString() },
				new FieldChange { FieldName = "relinkedTrashedItemCount", OldValue = null, NewValue = trashedCount.ToString() },
				new FieldChange { FieldName = "clearedMatchScoreCount", OldValue = null, NewValue = clearedMatchScoreCount.ToString() },
				new FieldChange { FieldName = "relinkedTemplateCount", OldValue = null, NewValue = templates.Count.ToString() },
			],
			now);

		await context.SaveChangesAsync(cancellationToken);
		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken);
		}
		await committedChangePublisher.PublishAsync(new(
			CommittedEntityType.NormalizedDescription,
			CommittedChangeType.Updated));

		// The returned count keeps its established meaning — live items re-linked — so the
		// admin-facing "N items re-linked" number still matches what a report would show.
		// Trashed rows are repointed for integrity but deliberately not counted here; the audit
		// entry reports them separately so the discrepancy is inspectable rather than invisible.
		return liveCount;
	}

	public const string SplitRequiresAtLeastOneItem = "At least one receipt item must be selected to split.";

	/// <summary>
	/// Detaches <paramref name="receiptItemIds"/> into a single new canonical entry named
	/// <paramref name="name"/>, and re-points every one of them at it (RECEIPTS-877).
	/// </summary>
	/// <remarks>
	/// The name is the caller's, not derived from the selection. A multi-item split routinely
	/// covers heterogeneous raw text ("MILK 2%", "milk gal", "WHOLE MILK"), where no automatic
	/// rule produces a name anyone would want.
	///
	/// All-or-nothing: an unknown id throws before anything is written, so a split either moves
	/// the whole selection or none of it. A partial split would leave the reviewer looking at a
	/// half-corrected group with no indication of which half moved.
	/// </remarks>
	/// <exception cref="KeyNotFoundException">Any id does not exist.</exception>
	/// <exception cref="ArgumentException">The selection is empty or the name is blank.</exception>
	public async Task<NormalizedDescriptionDetail> SplitAsync(
		IReadOnlyList<Guid> receiptItemIds,
		string name,
		CancellationToken cancellationToken)
	{
		if (receiptItemIds is null || receiptItemIds.Count == 0)
		{
			throw new ArgumentException(SplitRequiresAtLeastOneItem, nameof(receiptItemIds));
		}

		// Trim surrounding whitespace so callers cannot create
		// whitespace-divergent duplicates via Split.
		string canonicalName = (name ?? string.Empty).Trim();
		if (string.IsNullOrEmpty(canonicalName))
		{
			throw new ArgumentException(NormalizedDescription.CanonicalNameCannotBeEmpty, nameof(name));
		}

		using ApplicationDbContext read = contextFactory.CreateDbContext();

		List<Guid> distinctIds = [.. receiptItemIds.Distinct()];
		List<ReceiptItemEntity> observedItems = await read.ReceiptItems
			.IgnoreAutoIncludes()
			.Where(r => distinctIds.Contains(r.Id))
			.ToListAsync(cancellationToken);

		if (observedItems.Count != distinctIds.Count)
		{
			throw new KeyNotFoundException(ReceiptItemNotFound);
		}


		Guid[] parentIds = observedItems.Select(item => item.ReceiptId).Distinct().ToArray();
		var parentRevisions = await read.Receipts.IgnoreAutoIncludes().Where(parent => parentIds.Contains(parent.Id))
			.ToDictionaryAsync(parent => parent.Id, parent => parent.NormalizationRevision, cancellationToken);

		// Generate an embedding for the split item's raw description if possible, so the
		// new identity remains available to advisory candidate retrieval.
		Vector? embeddingVector = null;
		if (embeddingService.IsConfigured)
		{
			float[]? data = await TryGenerateEmbeddingAsync(canonicalName, cancellationToken);
			if (data is { Length: > 0 })
			{
				embeddingVector = new Vector(data);
			}
		}

		var observedTarget = await FindExactCaseInsensitiveAsync(read, canonicalName, cancellationToken);
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		await using IDbContextTransaction? transaction = context.Database.IsRelational()
			? await context.Database.BeginTransactionAsync(cancellationToken) : null;
		await NormalizationWriteGuard.AcquireWriteGateAsync(context, cancellationToken);
		NormalizedDescriptionEntity? existing = await FindExactCaseInsensitiveAsync(context, canonicalName, cancellationToken);
		Guid[] targetIds = observedItems.Where(item => item.NormalizedDescriptionId.HasValue).Select(item => item.NormalizedDescriptionId!.Value)
			.Concat(existing is null ? [] : new[] { existing.Id }).Distinct().Order().ToArray();
		await NormalizationWriteGuard.LockTargetsAsync(context, targetIds, cancellationToken);
		if (existing is not null) { await context.Entry(existing).ReloadAsync(cancellationToken); }
		if (existing?.Id != observedTarget?.Id || existing?.NormalizationRevision != observedTarget?.NormalizationRevision
			|| existing is not null && existing.Status != NormalizedDescriptionStatus.Active)
		{ throw new NormalizationCatalogException(409, "split_target_changed", "The chosen identity changed or is not active. Review the selection again."); }
		await ReceiptFactLock.AcquireAsync(context, parentIds, cancellationToken);
		var currentParents = await context.Receipts.IgnoreAutoIncludes().Where(parent => parentIds.Contains(parent.Id))
			.ToDictionaryAsync(parent => parent.Id, parent => parent.NormalizationRevision, cancellationToken);
		if (currentParents.Count != parentRevisions.Count || parentRevisions.Any(pair => currentParents.GetValueOrDefault(pair.Key) != pair.Value))
		{ throw new NormalizationCatalogException(409, "split_source_changed", "A selected receipt changed. Review the selection again."); }

		await NormalizationWriteGuard.LockItemsAsync(context, distinctIds.ToArray(), cancellationToken);
		List<ReceiptItemEntity> items = await context.ReceiptItems.IgnoreAutoIncludes().Where(item => distinctIds.Contains(item.Id)).ToListAsync(cancellationToken);
		if (items.Count != observedItems.Count || items.Any(item => observedItems.Single(old => old.Id == item.Id).NormalizationRevision != item.NormalizationRevision))
		{ throw new NormalizationCatalogException(409, "split_source_changed", "A selected purchase changed. Review the selection again."); }
		string actor = context.ReceiptWriteActor ?? throw new NormalizationCatalogException(403, "review_actor_required", "An enabled signed-in user is required to confirm a normalization choice.");
		bool wasInserted = existing is null;
		NormalizedDescriptionEntity created = existing ?? new()
		{
			Id = Guid.NewGuid(),
			CanonicalName = canonicalName,
			Status = NormalizedDescriptionStatus.Active,
			Embedding = embeddingVector,
			EmbeddingModelVersion = embeddingVector is null ? null : OnnxEmbeddingService.EmbeddingSpaceFingerprint,
			CreatedAt = DateTimeOffset.UtcNow
		};
		if (wasInserted)
		{
			if (await context.NormalizedDescriptions.AnyAsync(target => target.DisplayLabel != null && target.DisplayLabel.ToLower() == canonicalName.ToLower(), cancellationToken))
			{ throw new NormalizationCatalogException(409, "normalization_name_exists", "A catalog identity already uses that display name."); }
			context.NormalizedDescriptions.Add(created);
			// Insert now so a concurrent duplicate surfaces here, without committing this transaction.
			try
			{
				await context.SaveChangesAsync(cancellationToken);
			}
			catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException
			{ SqlState: Npgsql.PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_NormalizedDescriptions_CanonicalName_Lower" })
			{
				// SaveChanges rolled back its savepoint and detached unsuccessful automatic audits.
				// Reuse only the exact active winner; never retry the insert or commit a partial split.
				context.Entry(created).State = EntityState.Detached;
				var winner = await FindExactCaseInsensitiveAsync(context, canonicalName, cancellationToken);
				if (winner is null) { throw; }
				await NormalizationWriteGuard.LockTargetsAsync(context, [winner.Id], cancellationToken);
				await context.Entry(winner).ReloadAsync(cancellationToken);
				if (winner.Status != NormalizedDescriptionStatus.Active
					|| !string.Equals(winner.CanonicalName, canonicalName, StringComparison.OrdinalIgnoreCase))
				{ throw new NormalizationCatalogException(409, "split_target_changed", "The chosen identity changed or is not active. Review the selection again."); }
				created = winner;
				wasInserted = false;
			}
		}

		// Captured before the repoint below overwrites them — this is the only moment the "split out
		// of what?" answer exists in memory. A null origin is an unlinked item, which is a
		// legitimate thing to split rather than an error, and a multi-item selection can span
		// several source rows.
		// Counted here, not after the loop below: the repoint overwrites the very column these
		// counts are derived from.
		Dictionary<Guid, int> originCounts = items
			.Where(r => r.NormalizedDescriptionId.HasValue)
			.GroupBy(r => r.NormalizedDescriptionId!.Value)
			.ToDictionary(g => g.Key, g => g.Count());
		List<Guid> originIds = [.. originCounts.Keys];

		// Repoint, and clear the match score for the reason MergeAsync does: it was measured
		// against the row each item is leaving.
		foreach (ReceiptItemEntity moved in items)
		{
			moved.NormalizedDescriptionId = created.Id;
			moved.NormalizedDescriptionMatchScore = null;
		}

		Dictionary<Guid, string> originNames = await context.NormalizedDescriptions
			.AsNoTracking()
			.Where(e => originIds.Contains(e.Id))
			.ToDictionaryAsync(e => e.Id, e => e.DisplayLabel ?? e.CanonicalName, cancellationToken);

		// A split is mechanically a Create plus N Updates, which says nothing about what was
		// detached from what. Record it on the new row and on each source row, for the same reason
		// merges are: so the trail reads from either side (RECEIPTS-890).
		//
		// Origins equal to the created row are dropped. InsertAsync returns any existing row with
		// this canonical name — including a source row itself, when the chosen name matches its
		// canonical name but for case or surrounding whitespace. Those items did not move, and an
		// entry would record a row as having been split out of itself.
		List<Guid> movedFrom = [.. originIds.Where(id => id != created.Id)];

		// Two cases warrant an entry: something was detached from another row, or previously
		// unlinked items were gathered into a new one. The case with no entry is the no-op — every
		// selected item already belonged to the row the chosen name resolves to.
		bool anythingMoved = movedFrom.Count > 0 || originCounts.Count < items.Count;

		if (anythingMoved)
		{
			DateTimeOffset now = DateTimeOffset.UtcNow;

			// The selection can also land on a pre-existing *different* row, which is a re-link
			// rather than a split into a new description. Recording that distinction keeps the
			// entry from implying a row was created when none was.
			FieldChange targetOrigin = new()
			{
				FieldName = "targetWasExistingRow",
				OldValue = null,
				NewValue = wasInserted ? "false" : "true",
			};

			// Null, not a placeholder string, when the selection had no origin row. Absence is
			// already how the rest of the trail says "nothing to point at", and inventing
			// "(unlinked)" would read like the name of a row somebody could go look up.
			string? originSummary = movedFrom.Count == 0
				? null
				: string.Join(", ", movedFrom.Select(id => originNames.TryGetValue(id, out string? n) ? n : id.ToString()));

			context.AddSemanticAuditEntry(
				NormalizedDescriptionEntityType,
				created.Id.ToString(),
				AuditAction.Split,
				[
					new FieldChange { FieldName = "splitFrom", OldValue = originSummary, NewValue = canonicalName },
					new FieldChange { FieldName = "splitFromIds", OldValue = string.Join(",", movedFrom), NewValue = null },
					new FieldChange { FieldName = "receiptItemIds", OldValue = null, NewValue = string.Join(",", items.Select(r => r.Id)) },
					new FieldChange { FieldName = "receiptItemCount", OldValue = null, NewValue = items.Count.ToString() },
					targetOrigin,
				],
				now);

			foreach (Guid originId in movedFrom)
			{
				int movedFromThisOrigin = originCounts[originId];
				context.AddSemanticAuditEntry(
					NormalizedDescriptionEntityType,
					originId.ToString(),
					AuditAction.Split,
					[
						new FieldChange { FieldName = "splitOut", OldValue = originNames.TryGetValue(originId, out string? originName) ? originName : null, NewValue = canonicalName },
						new FieldChange { FieldName = "splitToId", OldValue = null, NewValue = created.Id.ToString() },
						new FieldChange { FieldName = "receiptItemCount", OldValue = null, NewValue = movedFromThisOrigin.ToString() },
						targetOrigin,
					],
					now);
			}
		}

		await context.RecordReviewedAssignmentsAsync(items, actor, cancellationToken);
		int changed = await context.SaveChangesAsync(cancellationToken);
		if (transaction is not null) { await transaction.CommitAsync(cancellationToken); }
		if (changed > 0)
		{
			await committedChangePublisher.PublishAsync(new(
				CommittedEntityType.NormalizedDescription,
				CommittedChangeType.Updated));
		}

		// Re-read through the same projection the list endpoint uses so the caller gets a truthful
		// LinkedItemCount for the row it just created, rather than a count derived from the
		// selection that would drift the moment Split's semantics change. One extra query on an
		// admin-only action.
		DetailRow? row = await ProjectDetails(context)
			.FirstOrDefaultAsync(d => d.Id == created.Id, cancellationToken);
		if (row is not null)
		{
			await PopulateSampleRawDescriptionsAsync(context, [row], cancellationToken);
		}

		// The row was just committed in this same context, so a miss here means something deleted
		// it out from under us mid-call. Fall back to the in-memory entity with the evidence we
		// know first-hand rather than throwing.
		return row?.ToDetail()
			?? new NormalizedDescriptionDetail(mapper.ToDomain(created), items.Count, NearestNeighbourName: null, [canonicalName]);
	}

	public async Task<bool> UpdateStatusAsync(Guid id, NormalizedDescriptionStatus status, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		await using IDbContextTransaction? transaction = context.Database.IsRelational()
			? await context.Database.BeginTransactionAsync(cancellationToken) : null;
		// Serialize with automatic attachment before reading status or scanning children.
		await NormalizationWriteGuard.LockTargetsAsync(context, [id], cancellationToken);
		NormalizedDescriptionEntity? entity = await context.NormalizedDescriptions
			.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
		if (entity is null || entity.Status == status)
		{
			return false;
		}

		NormalizedDescriptionStatus previous = entity.Status;
		entity.Status = status;
		if (status != NormalizedDescriptionStatus.Active)
		{
			foreach (var mapping in await context.NormalizationSourceMappings.Where(mapping => mapping.TargetId == id && mapping.State == "active").ToListAsync(cancellationToken))
			{
				mapping.State = "revoked";
				mapping.Revision = Guid.NewGuid();
			}
		}

		if (status == NormalizedDescriptionStatus.Rejected)
		{
			await DetachItemsForRejectionAsync(context, entity, previous, cancellationToken);
		}

		await context.SaveChangesAsync(cancellationToken);
		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken);
		}
		await committedChangePublisher.PublishAsync(new(
			CommittedEntityType.NormalizedDescription,
			CommittedChangeType.Updated,
			id));
		return true;
	}

	/// <summary>
	/// Unlinks every receipt item from a row being rejected, so the items fall back to
	/// unnormalized while the row survives as a tombstone (RECEIPTS-876).
	/// </summary>
	/// <remarks>
	/// Two details are load-bearing.
	///
	/// The match score is cleared alongside the FK. ON DELETE SET NULL would not help here — the
	/// row is not being deleted — and a live item carrying a score with no description to explain
	/// it is exactly the inconsistent state RECEIPTS-883 exists to prevent.
	///
	/// IgnoreQueryFilters reaches past soft-delete so trashed items are detached too. A trashed
	/// item left pointing at a tombstone would come back from the recycle bin linked to a row the
	/// reviewer rejected, and no report would ever show it again.
	/// </remarks>
	private static async Task DetachItemsForRejectionAsync(
		ApplicationDbContext context,
		NormalizedDescriptionEntity entity,
		NormalizedDescriptionStatus previous,
		CancellationToken cancellationToken)
	{
		IQueryable<ReceiptItemEntity> itemQuery = context.Database.IsNpgsql()
			? context.ReceiptItems.FromSqlInterpolated($"""
				SELECT * FROM receipts."ReceiptItems" WHERE "NormalizedDescriptionId" = {entity.Id}
				ORDER BY "Id" FOR UPDATE
				""")
			: context.ReceiptItems.Where(item => item.NormalizedDescriptionId == entity.Id);
		List<ReceiptItemEntity> items = await itemQuery.IgnoreQueryFilters().IgnoreAutoIncludes().ToListAsync(cancellationToken);

		int unlinkedItemCount = 0;
		foreach (ReceiptItemEntity item in items)
		{
			if (item.DeletedAt is null)
			{
				unlinkedItemCount++;
			}

			item.NormalizedDescriptionId = null;
			item.NormalizedDescriptionMatchScore = null;
		}

		// Templates declaring this text are unlinked too (RECEIPTS-881). The row survives as a
		// tombstone, so nothing in the database forces this — a template would keep pointing at a
		// Rejected entry and go on stamping new receipt items with it, which is precisely the
		// resolver bypass working against the reviewer's decision.
		//
		// The template itself is left alone. Rejecting a canonical entry is a judgement about
		// receipt text, not about the user's curated entry-time defaults, and silently deleting
		// someone's template as a side effect of an admin action on a different screen would be a
		// much bigger surprise than an unlinked one. An unlinked template simply re-links on its
		// next use — which, if the text is still tombstoned, GetOrCreateForTemplateAsync treats as
		// the user contradicting the rejection on purpose.
		IQueryable<ItemTemplateEntity> templateQuery = context.Database.IsNpgsql()
			? context.ItemTemplates.FromSqlInterpolated($"""
				SELECT * FROM library."ItemTemplates" WHERE "NormalizedDescriptionId" = {entity.Id}
				ORDER BY "Id" FOR UPDATE
				""")
			: context.ItemTemplates.Where(template => template.NormalizedDescriptionId == entity.Id);
		List<ItemTemplateEntity> templates = await templateQuery.IgnoreQueryFilters().ToListAsync(cancellationToken);

		foreach (ItemTemplateEntity template in templates)
		{
			template.NormalizedDescriptionId = null;
		}

		// A rejection is a reviewer's judgement, not the mechanical status flip the automatic
		// audit would record. Naming it explicitly keeps "who decided this text was garbage, and
		// how much data moved" answerable later (RECEIPTS-890).
		context.AddSemanticAuditEntry(
			NormalizedDescriptionEntityType,
			entity.Id.ToString(),
			AuditAction.Update,
			[
				new FieldChange { FieldName = "operation", OldValue = null, NewValue = "Reject" },
				new FieldChange { FieldName = "status", OldValue = previous.ToString(), NewValue = NormalizedDescriptionStatus.Rejected.ToString() },
				new FieldChange { FieldName = "canonicalName", OldValue = null, NewValue = entity.CanonicalName },
				new FieldChange { FieldName = "unlinkedItemCount", OldValue = null, NewValue = unlinkedItemCount.ToString() },
				new FieldChange { FieldName = "unlinkedTrashedItemCount", OldValue = null, NewValue = (items.Count - unlinkedItemCount).ToString() },
				new FieldChange { FieldName = "unlinkedTemplateCount", OldValue = null, NewValue = templates.Count.ToString() },
			],
			DateTimeOffset.UtcNow);
	}

	public const string RenameTargetNotFound = "Normalized description not found.";
	public const string DisplayNameAlreadyTaken = "Another normalized description already displays that name.";

	/// <summary>
	/// Sets or clears a row's display label (RECEIPTS-876). Null clears it, so the row falls back
	/// to showing its matched text.
	/// </summary>
	/// <remarks>
	/// The embedding and <c>CanonicalName</c> are deliberately untouched. Renaming is cosmetic by
	/// construction here, so no rename can change which receipt text resolves to this row.
	/// </remarks>
	/// <exception cref="KeyNotFoundException">No row with this id.</exception>
	/// <exception cref="ArgumentException">The label is whitespace-only or too long.</exception>
	/// <exception cref="InvalidOperationException">Another row already displays that name.</exception>
	public async Task<NormalizedDescriptionDetail> RenameAsync(
		Guid id,
		string? displayLabel,
		CancellationToken cancellationToken)
	{
		string? trimmed = displayLabel?.Trim();
		NormalizedDescription.ValidateDisplayLabel(trimmed);

		using ApplicationDbContext context = contextFactory.CreateDbContext();

		NormalizedDescriptionEntity? entity = await context.NormalizedDescriptions
			.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
		if (entity is null)
		{
			throw new KeyNotFoundException(RenameTargetNotFound);
		}

		trimmed = await PrepareDisplayLabelAsync(context, entity, trimmed, cancellationToken);

		entity.DisplayLabel = trimmed;

		try
		{
			int changed = await context.SaveChangesAsync(cancellationToken);
			if (changed > 0)
			{
				await committedChangePublisher.PublishAsync(new(
					CommittedEntityType.NormalizedDescription,
					CommittedChangeType.Updated,
					id));
			}
		}
		catch (DbUpdateException)
		{
			// Another writer claimed the same display name between the check above and this save.
			throw new InvalidOperationException(DisplayNameAlreadyTaken);
		}

		DetailRow? row = await ProjectDetails(context)
			.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
		if (row is not null)
		{
			await PopulateSampleRawDescriptionsAsync(context, [row], cancellationToken);
		}

		return row?.ToDetail()
			?? new NormalizedDescriptionDetail(mapper.ToDomain(entity), LinkedItemCount: 0, NearestNeighbourName: null, []);
	}

	internal static async Task<string?> PrepareDisplayLabelAsync(ApplicationDbContext context, NormalizedDescriptionEntity entity,
		string? displayLabel, CancellationToken cancellationToken)
	{
		string? trimmed = displayLabel?.Trim();
		NormalizedDescription.ValidateDisplayLabel(trimmed);
		if (trimmed is not null && string.Equals(trimmed, entity.CanonicalName, StringComparison.OrdinalIgnoreCase)) { trimmed = null; }
		// Clearing also claims the canonical name as the effective display name.
		if (await DisplayNameTakenAsync(context, trimmed ?? entity.CanonicalName, entity.Id, cancellationToken))
		{ throw new InvalidOperationException(DisplayNameAlreadyTaken); }
		return trimmed;
	}

	// Compares against each row's effective display name, matching the unique index on
	// lower(COALESCE("DisplayLabel","CanonicalName")). Comparing against DisplayLabel alone would
	// miss the common collision: renaming one row onto another row's un-renamed name.
	private static async Task<bool> DisplayNameTakenAsync(
		ApplicationDbContext context,
		string displayLabel,
		Guid excludingId,
		CancellationToken cancellationToken)
	{
		string lowered = displayLabel.ToLowerInvariant();
		return await context.NormalizedDescriptions
			.AsNoTracking()
			.AnyAsync(
				e => e.Id != excludingId
					&& (e.DisplayLabel == null
						? e.CanonicalName.ToLower()
						: e.DisplayLabel.ToLower()) == lowered,
				cancellationToken);
	}

	public const string LinkTemplateTemplateNotFound = "Item template not found.";
	public const string LinkTemplateDescriptionNotFound = "Normalized description not found.";
	public const string CannotLinkTemplateToRejected = "This entry was rejected. Reinstate it before linking a template, or create the template from its own name instead.";
	public const string CannotLinkTemplateToRejectedEntry = "That template's own entry was rejected. Reinstate it from the registry first — linking here would silently undo that rejection.";
	public const string TemplateNameCollidesWithDisplayName = "Another entry is already displayed as \"{0}\", so this template cannot be given an entry of its own. Rename that entry first.";

	/// <summary>
	/// Records that <paramref name="descriptionId"/> is the item an existing template already
	/// describes (RECEIPTS-930).
	/// </summary>
	/// <remarks>
	/// A template's canonical entry is the row whose name matches the template's — that is the
	/// invariant <c>ItemTemplateService</c> re-establishes on every create and update. So this
	/// resolves that entry, points the template at it, and consolidates the caller's row into it.
	///
	/// It deliberately does <em>not</em> simply set the template's FK to
	/// <paramref name="descriptionId"/>, which is the obvious reading of "link". Two reasons, both
	/// load-bearing:
	///
	/// 1. It would not survive. <c>ItemTemplateService.UpdateAsync</c> re-resolves the link from
	///    the template's name on every save, so the next edit to that template — a price change,
	///    a category fix, anything — would silently point it back at its own entry. A link that
	///    disappears on an unrelated edit is worse than no link, because nobody would connect
	///    the two events.
	/// 2. It would not do what the caller wants. Pointing the FK affects items entered from the
	///    template <em>in future</em>. The receipt items already sitting on this row would stay
	///    where they are, so the two would go on reporting as separate buckets — which is the
	///    duplication being complained about.
	///
	/// When the row already is the template's entry, there is nothing to consolidate and only the
	/// FK is set. That case is reported separately rather than smoothed over: the caller pointed at
	/// a row, and whether that row still exists afterwards is not a detail.
	///
	/// Which row counts as "the template's entry" is <see cref="ResolveTemplateEntryIdAsync"/>'s
	/// job, and it is not simply the row named after the template — see the remarks there.
	/// </remarks>
	/// <exception cref="KeyNotFoundException">The row or the (live) template does not exist.</exception>
	/// <exception cref="InvalidOperationException">
	/// Either row is a rejected tombstone, or the template's name is already taken as another row's
	/// display name.
	/// </exception>
	public async Task<LinkTemplateResult> LinkTemplateAsync(
		Guid descriptionId,
		Guid itemTemplateId,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();

		// The default query filter excludes soft-deleted templates, which is the behaviour we want:
		// linking to something in the recycle bin would produce a link that evaporates when it is
		// purged, and the picker never offers one.
		ItemTemplateEntity? template = await context.ItemTemplates
			.FirstOrDefaultAsync(t => t.Id == itemTemplateId, cancellationToken);
		if (template is null)
		{
			throw new KeyNotFoundException(LinkTemplateTemplateNotFound);
		}

		NormalizedDescriptionEntity? target = await context.NormalizedDescriptions
			.FirstOrDefaultAsync(e => e.Id == descriptionId, cancellationToken);
		if (target is null)
		{
			throw new KeyNotFoundException(LinkTemplateDescriptionNotFound);
		}

		// Consolidating a tombstone away would delete the only record that a reviewer decided this
		// text was not worth an entry. Preserve that explicit rejection decision.
		if (target.Status == NormalizedDescriptionStatus.Rejected)
		{
			throw new InvalidOperationException(CannotLinkTemplateToRejected);
		}

		Guid canonicalId = await ResolveTemplateEntryIdAsync(context, template, cancellationToken);
		bool merged = canonicalId != descriptionId;

		// Merge FIRST, and commit the foreign key only once it has succeeded.
		//
		// The reverse order looks tidier — put the template on its entry, then move the items — but
		// the two run in separate DbContexts and therefore separate transactions, so a merge failure
		// (a concurrent edit conflicts, the caller goes away) would leave the template
		// permanently torn off whatever entry it previously declared, with the row still in the queue
		// and no audit entry to explain the move. Every receipt item entered from that template would
		// then be filed under a bucket nobody chose. Failing before the FK write instead leaves the
		// template exactly where it was.
		//
		// MergeAsync re-points templates pointing at the discarded row, so a template already on
		// `descriptionId` follows the items automatically; the explicit write below then confirms the
		// same value rather than fighting it.
		int itemsRelinked = merged
			? await MergeAsync(canonicalId, descriptionId, cancellationToken)
			: 0;

		template.NormalizedDescriptionId = canonicalId;

		// Written with the FK in one save, so the trail cannot claim a link that was not committed.
		// MergeAsync files its own pair of entries for what physically moved; this one records why —
		// that an operator recognised the row as a template's item — which is the part no mechanical
		// trail can reconstruct (RECEIPTS-890).
		context.AddSemanticAuditEntry(
			NormalizedDescriptionEntityType,
			canonicalId.ToString(),
			AuditAction.Update,
			[
				new FieldChange { FieldName = "operation", OldValue = null, NewValue = "LinkItemTemplate" },
				new FieldChange { FieldName = "itemTemplateId", OldValue = null, NewValue = template.Id.ToString() },
				new FieldChange { FieldName = "itemTemplateName", OldValue = null, NewValue = template.Name },
				// Null rather than the same id repeated when nothing was consolidated: absence is
				// already how the rest of the trail says "no other row was involved".
				new FieldChange { FieldName = "consolidatedFromId", OldValue = merged ? descriptionId.ToString() : null, NewValue = null },
				new FieldChange { FieldName = "relinkedItemCount", OldValue = null, NewValue = itemsRelinked.ToString() },
			],
			DateTimeOffset.UtcNow);
		await context.SaveChangesAsync(cancellationToken);
		await committedChangePublisher.PublishAsync(new(
			CommittedEntityType.NormalizedDescription,
			CommittedChangeType.Updated,
			canonicalId));

		// Re-read through the shared projection so the caller gets a truthful LinkedItemCount and
		// the template evidence it just created, rather than numbers assembled from the pieces
		// above. The row was committed in this call, so a miss means something deleted it
		// concurrently — a genuine 404 rather than something to paper over.
		NormalizedDescriptionDetail survivor = await GetByIdAsync(canonicalId, cancellationToken)
			?? throw new KeyNotFoundException(LinkTemplateDescriptionNotFound);

		return new LinkTemplateResult(survivor, itemsRelinked, merged);
	}

	/// <summary>
	/// The entry a template already declares, or the one its name resolves to, creating it if needed.
	/// </summary>
	/// <remarks>
	/// The FK is consulted first, and that ordering is load-bearing. "A template's entry is the row
	/// named after it" is only an invariant at create and update time — <see cref="MergeAsync"/>
	/// breaks it deliberately, re-pointing templates at the survivor when their entry is merged away.
	/// After "Gallon of Milk" is merged into "Milk", the template still declares "Milk" and holds all
	/// of its history there, while no row named "Gallon of Milk" exists any more.
	///
	/// Resolving by name alone in that state would create a fresh empty "Gallon of Milk", move the
	/// template onto it, and consolidate the reviewer's row into it — leaving three buckets where
	/// they were trying to end up with one, and silently detaching the template from its own history.
	/// Reading the FK first makes the linked case exact and leaves name resolution for what it is
	/// actually needed for: a template that has never been linked at all.
	/// </remarks>
	private async Task<Guid> ResolveTemplateEntryIdAsync(
		ApplicationDbContext context,
		ItemTemplateEntity template,
		CancellationToken cancellationToken)
	{
		if (template.NormalizedDescriptionId is { } linkedId)
		{
			// Confirmed against the table rather than trusted: the FK is ON DELETE SET NULL, but a
			// row deleted by another request between this read and the merge would otherwise send a
			// stale id into MergeAsync as the keeper.
			NormalizedDescriptionEntity? linked = await context.NormalizedDescriptions
				.AsNoTracking()
				.FirstOrDefaultAsync(e => e.Id == linkedId, cancellationToken);

			// A Rejected row here would mean a tombstone somehow kept its template, which
			// DetachItemsForRejectionAsync exists to prevent. Falling through to name resolution
			// rather than merging into it keeps the tombstone guard below authoritative.
			if (linked is { Status: not NormalizedDescriptionStatus.Rejected })
			{
				return linked.Id;
			}
		}

		string name = template.Name.Trim();
		NormalizedDescriptionEntity? byName = await FindExactCaseInsensitiveAsync(context, name, cancellationToken);

		// GetOrCreateForTemplateAsync reinstates a tombstone it finds by name, and on *that* path it
		// is right to: the user typed the name into a template, deliberately contradicting the
		// rejection. Here they picked an existing template out of a list while looking at a
		// differently-named row, so nothing about the gesture says "un-reject this". Refusing keeps
		// the promise the endpoint documents — rejected rows are not linkable — which the guard on
		// the caller's row alone did not actually make good on.
		if (byName is { Status: NormalizedDescriptionStatus.Rejected })
		{
			throw new InvalidOperationException(CannotLinkTemplateToRejectedEntry);
		}

		if (byName is not null)
		{
			return byName.Id;
		}

		// About to insert. The unique index is on lower(COALESCE("DisplayLabel","CanonicalName")), so
		// a row somebody renamed *to* this template's name collides even though no CanonicalName
		// matches — and InsertAsync's race handler only recovers from a CanonicalName collision, so
		// the DbUpdateException would escape as a 500 that no amount of retrying could clear.
		// Checked up front so the caller gets a message naming the actual obstacle.
		if (await DisplayNameTakenAsync(context, name, Guid.Empty, cancellationToken))
		{
			throw new InvalidOperationException(string.Format(TemplateNameCollidesWithDisplayName, name));
		}

		try
		{
			NormalizedDescription created = await GetOrCreateForTemplateAsync(template.Name, cancellationToken);
			return created.Id;
		}
		catch (DbUpdateException)
		{
			// Lost the race the check above was guarding: another writer claimed that display name
			// between the check and the insert. Same message, still a 400 — retrying unchanged cannot
			// succeed either way.
			throw new InvalidOperationException(string.Format(TemplateNameCollidesWithDisplayName, name));
		}
	}

	public async Task<RequeuePendingPreview> PreviewRequeuePendingAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();

		List<Guid> pendingIds = await context.NormalizedDescriptions
			.AsNoTracking()
			.Where(e => e.Status == NormalizedDescriptionStatus.PendingReview)
			.Select(e => e.Id)
			.ToListAsync(cancellationToken);

		// Live items only, matching the counts RequeuePendingAsync reports back. The default
		// query filter already excludes trashed rows here; the requeue itself deliberately
		// reaches past it to repoint them too (see RequeuePendingAsync).
		var counts = await context.ReceiptItems
			.AsNoTracking()
			.IgnoreAutoIncludes()
			.Where(r => r.NormalizedDescription!.Status == NormalizedDescriptionStatus.PendingReview)
			.GroupBy(_ => 1)
			.Select(g => new
			{
				Linked = g.Count(),
				Stale = g.Count(r => r.NormalizedDescriptionMatchScore != null),
			})
			.FirstOrDefaultAsync(cancellationToken);

		int linkedItemCount = counts?.Linked ?? 0;
		int staleMatchScoreCount = counts?.Stale ?? 0;

		// Retained numeric response fields are deprecated compatibility fields. Automatic
		// similarity rematching is retired; zero indicates no scheduled rematch estimate.
		const int cycles = 0;
		const int seconds = 0;

		return new RequeuePendingPreview(
			pendingIds.Count,
			ComputePendingFingerprint(pendingIds),
			linkedItemCount,
			staleMatchScoreCount,
			cycles,
			seconds);
	}

	public async Task<RequeuePendingResult?> RequeuePendingAsync(string expectedFingerprint, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		await using IDbContextTransaction? transaction = context.Database.IsRelational()
			? await context.Database.BeginTransactionAsync(cancellationToken) : null;
		await NormalizationWriteGuard.AcquireWriteGateAsync(context, cancellationToken);

		List<NormalizedDescriptionEntity> pending = await context.NormalizedDescriptions
			.Where(e => e.Status == NormalizedDescriptionStatus.PendingReview)
			.ToListAsync(cancellationToken);

		// Optimistic guard against a stale caller. The admin previewed a specific set of rows and
		// confirmed THAT set; anything else must be re-read before it is destroyed.
		//
		// Comparing identities rather than counts is load-bearing. Suppose the preview showed
		// {P1,P2,P3,P4}; a second admin approves P1 through the Review Queue while the resolver
		// queues a new near-miss P5. The set is now {P2,P3,P4,P5} — still four rows, so a count
		// check would sail straight through and delete P5, which no operator ever saw.
		if (!string.Equals(ComputePendingFingerprint(pending.Select(e => e.Id)), expectedFingerprint, StringComparison.Ordinal))
		{
			return null;
		}

		if (pending.Count == 0)
		{
			// Re-runnable by design: a second pass with nothing pending is a no-op, not an error.
			// The empty set has a stable fingerprint of its own, so this path is still guarded.
			return new RequeuePendingResult(0, 0, 0);
		}

		List<Guid> pendingIds = [.. pending.Select(e => e.Id)];
		await NormalizationWriteGuard.LockTargetsAsync(context, pendingIds.ToArray(), cancellationToken);

		// IgnoreQueryFilters so trashed items are repointed as well. The FK is DeleteBehavior.SetNull,
		// so a trashed row left pointing at a deleted description would have its link nulled by the
		// database and come back from the recycle bin unlinked with no error raised — but with its
		// stale match score intact, which is exactly the inconsistent state this issue exists to
		// prevent. Same class of bug as the soft-deleted-transaction stranding fixed in
		// AccountMergeService (RECEIPTS-801) and guarded in MergeAsync above.
		List<ReceiptItemEntity> items = await context.ReceiptItems
			.IgnoreQueryFilters()
			.IgnoreAutoIncludes()
			.Where(r => r.NormalizedDescriptionId != null && pendingIds.Contains(r.NormalizedDescriptionId.Value))
			.ToListAsync(cancellationToken);

		int unlinkedItemCount = 0;
		int clearedMatchScoreCount = 0;

		foreach (ReceiptItemEntity item in items)
		{
			bool isLive = item.DeletedAt is null;

			// Null the score explicitly rather than relying on the delete cascade. ON DELETE SET NULL
			// covers the FK only — NormalizedDescriptionMatchScore is a plain column, so the cascade
			// would leave a score behind with no description to explain it. Doing both here keeps the
			// pair consistent within the single transaction below.
			if (item.NormalizedDescriptionMatchScore is not null)
			{
				item.NormalizedDescriptionMatchScore = null;
				if (isLive)
				{
					clearedMatchScoreCount++;
				}
			}

			item.NormalizedDescriptionId = null;
			if (isLive)
			{
				unlinkedItemCount++;
			}
		}

		context.NormalizedDescriptions.RemoveRange(pending);

		// One semantic entry for the whole operation rather than per deleted row: the requeue is a
		// single operator decision, and N separate entries would bury that under the very
		// row-by-row noise this exists to summarise. The per-row Deletes are still auto-audited
		// alongside it, so nothing is lost — this adds the intent on top (RECEIPTS-890).
		//
		// EntityId is empty because no single row is the subject. The audit page keys its filter on
		// EntityType, so the entry is still reachable; there is simply no one id to attribute a
		// bulk delete to.
		context.AddSemanticAuditEntry(
			NormalizedDescriptionEntityType,
			string.Empty,
			AuditAction.Delete,
			[
				new FieldChange { FieldName = "operation", OldValue = null, NewValue = "RequeuePending" },
				new FieldChange { FieldName = "deletedDescriptionCount", OldValue = null, NewValue = pending.Count.ToString() },
				new FieldChange { FieldName = "unlinkedItemCount", OldValue = null, NewValue = unlinkedItemCount.ToString() },
				new FieldChange { FieldName = "clearedMatchScoreCount", OldValue = null, NewValue = clearedMatchScoreCount.ToString() },
				new FieldChange { FieldName = "unlinkedTrashedItemCount", OldValue = null, NewValue = (items.Count - unlinkedItemCount).ToString() },
			],
			DateTimeOffset.UtcNow);

		// Single SaveChanges: either the unlink, the score clear, the delete and the audit entry all
		// land, or none do. A partial commit would strand items pointing at deleted rows, or record
		// a requeue that did not happen.
		await context.SaveChangesAsync(cancellationToken);
		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken);
		}
		await committedChangePublisher.PublishAsync(new(
			CommittedEntityType.NormalizedDescription,
			CommittedChangeType.Deleted));

		return new RequeuePendingResult(pending.Count, unlinkedItemCount, clearedMatchScoreCount);
	}

	// Order-independent digest of a set of pending ids. Sorted before hashing so two callers that
	// read the same rows in different orders agree, and hashed rather than returned verbatim so the
	// token stays a fixed small size no matter how deep the review queue gets. SHA-256 is used as a
	// checksum here, not a security primitive — the value is opaque to clients either way.
	internal static string ComputePendingFingerprint(IEnumerable<Guid> ids)
	{
		string joined = string.Join(',', ids.OrderBy(id => id));
		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
	}

	// Scalar evidence projection shared by GetAllAsync / GetByIdAsync / SplitAsync
	// (RECEIPTS-873). Three things happen here that a naive implementation would get wrong:
	//
	//  1. No N+1. Scalar evidence stays in this query; sample descriptions are loaded by one
	//     bounded set query after the requested row/page is known. Npgsql cannot translate a
	//     correlated Distinct/OrderBy/ToList collection inside this projection (RECEIPTS-940).
	//     The existing IX_ReceiptItems_NormalizedDescriptionId index covers both queries.
	//  2. Soft-deleted receipt items are excluded automatically: the ReceiptItemEntity query filter
	//     applies inside subqueries, so a deleted item never inflates the count an admin sees.
	//  3. Embedding is not selected. The old GetAllAsync materialized whole entities, dragging a
	//     384-float vector per row across the wire for a list that never displays it.
	//
	private static IQueryable<DetailRow> ProjectDetails(ApplicationDbContext context) =>
		context.NormalizedDescriptions
			.AsNoTracking()
			.Select(e => new DetailRow
			{
				Id = e.Id,
				CanonicalName = e.CanonicalName,
				DisplayLabel = e.DisplayLabel,
				Status = e.Status,
				CreatedAt = e.CreatedAt,
				NearestNeighbourId = e.NearestNeighbourId,
				NearestNeighbourSimilarity = e.NearestNeighbourSimilarity,
				// The neighbour's display name, not its raw matched text — the reviewer is being
				// told which entry this nearly matched, and they know that entry by whatever it
				// is called on screen.
				NearestNeighbourName = e.NearestNeighbour == null
					? null
					: (e.NearestNeighbour.DisplayLabel ?? e.NearestNeighbour.CanonicalName),
				LinkedItemCount = context.ReceiptItems.Count(r => r.NormalizedDescriptionId == e.Id),
				// RECEIPTS-880. The latest receipt date this row still appears on. CreatedAt alone
				// cannot tell a two-year-old entry that is still matching this week's receipts
				// from one nothing has matched since. Null when nothing is linked — which is a
				// different statement from "last seen a long time ago".
				//
				// Guarded on Receipt != null rather than dereferencing blind: the receipt may be
				// soft-deleted (its query filter applies inside the subquery), and these
				// projections run under IgnoreAutoIncludes, so the navigation is not guaranteed
				// to be populated on every provider.
				LastSeen = context.ReceiptItems
					.Where(r => r.NormalizedDescriptionId == e.Id && r.Receipt != null)
					.Select(r => (DateOnly?)r.Receipt!.Date)
					.Max(),
				// RECEIPTS-930. The template, if any, that declares this row — read off the FK
				// ItemTemplate gained in RECEIPTS-881, so it states a recorded link rather than a
				// resemblance. Soft-deleted templates fall out via the entity's query filter, which
				// is what we want: a template sitting in the recycle bin is not evidence of anything.
				//
				// Ordered by name so the row a reviewer sees does not shuffle between refreshes when
				// several templates point here — same reason the samples above are ordered before
				// Take. Name and id are two subqueries rather than one because EF cannot project a
				// tuple out of a correlated FirstOrDefault; both hit IX_ItemTemplates_NormalizedDescriptionId.
				LinkedTemplateId = context.ItemTemplates
					.Where(t => t.NormalizedDescriptionId == e.Id)
					.OrderBy(t => t.Name)
					.Select(t => (Guid?)t.Id)
					.FirstOrDefault(),
				LinkedTemplateName = context.ItemTemplates
					.Where(t => t.NormalizedDescriptionId == e.Id)
					.OrderBy(t => t.Name)
					.Select(t => t.Name)
					.FirstOrDefault(),
				LinkedTemplateCount = context.ItemTemplates.Count(t => t.NormalizedDescriptionId == e.Id),
			});

	private static async Task PopulateSampleRawDescriptionsAsync(
		ApplicationDbContext context,
		IReadOnlyCollection<DetailRow> rows,
		CancellationToken cancellationToken)
	{
		Guid[] ids = [.. rows.Select(r => r.Id)];
		if (ids.Length == 0)
		{
			return;
		}

		const string sql =
			"""
			WITH distinct_samples AS (
			    SELECT DISTINCT
			        "NormalizedDescriptionId" AS "Id",
			        "Description"
			    FROM receipts."ReceiptItems"
			    WHERE "DeletedAt" IS NULL
			      AND "NormalizedDescriptionId" = ANY ({0})
			), ranked_samples AS (
			    SELECT
			        "Id",
			        "Description",
			        ROW_NUMBER() OVER (PARTITION BY "Id" ORDER BY "Description") AS row_number
			    FROM distinct_samples
			)
			SELECT "Id", "Description"
			FROM ranked_samples
			WHERE row_number <= {1}
			ORDER BY "Id", row_number
			""";

		List<SampleDescriptionRow> samples;
		if (context.Database.ProviderName == PostgreSQL)
		{
			samples = await context.Database
				.SqlQueryRaw<SampleDescriptionRow>(sql, ids, MaxSampleRawDescriptions)
				.ToListAsync(cancellationToken);
		}
		else
		{
			// Unit tests use EF InMemory, which cannot execute relational SQL. Keep an equivalent
			// provider-neutral path there; production PostgreSQL uses the bounded window query above.
			var fallbackSamples = await context.ReceiptItems
				.Where(r => r.NormalizedDescriptionId != null && ids.Contains(r.NormalizedDescriptionId.Value))
				.Select(r => new { Id = r.NormalizedDescriptionId!.Value, r.Description })
				.Distinct()
				.ToListAsync(cancellationToken);
			samples = [.. fallbackSamples.Select(r => new SampleDescriptionRow { Id = r.Id, Description = r.Description })];
		}
		Dictionary<Guid, List<string>> samplesById = samples
			.GroupBy(r => r.Id)
			.ToDictionary(
				g => g.Key,
				g => g.Select(r => r.Description).OrderBy(d => d).Take(MaxSampleRawDescriptions).ToList());

		foreach (DetailRow row in rows)
		{
			row.SampleRawDescriptions = samplesById.GetValueOrDefault(row.Id, []);
		}
	}

	private sealed class SampleDescriptionRow
	{
		public Guid Id { get; init; }
		public string Description { get; init; } = string.Empty;
	}

	public async Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		await using IDbContextTransaction? transaction = context.Database.IsRelational()
			? await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
			: null;
		string fingerprint = OnnxEmbeddingService.EmbeddingSpaceFingerprint;

		(int canonicalReady, int canonicalTotal) = await CatalogEmbeddingCoverage.CountAsync(context, cancellationToken);

		IQueryable<ItemTemplateEntity> templates = context.ItemTemplates
			.AsNoTracking()
			.IgnoreQueryFilters()
			.Where(entity => entity.DeletedAt == null && entity.Name.Length >= 2);
		IQueryable<ReceiptItemEntity> receiptItems = context.ReceiptItems
			.AsNoTracking()
			.IgnoreAutoIncludes()
			.IgnoreQueryFilters()
			.Where(entity => entity.DeletedAt == null && entity.Description.Length >= 2);

		int itemTotal = await templates.CountAsync(cancellationToken)
			+ await receiptItems.CountAsync(cancellationToken);
		int itemReady = await templates.CountAsync(
			entity => context.ItemEmbeddings.Any(embedding =>
				embedding.EntityType == "ItemTemplate"
				&& embedding.EntityId == entity.Id
				&& embedding.EntityText == entity.Name
				&& embedding.ModelVersion == fingerprint),
			cancellationToken)
			+ await receiptItems.CountAsync(
				entity => context.ItemEmbeddings.Any(embedding =>
					embedding.EntityType == "ReceiptItem"
					&& embedding.EntityId == entity.Id
					&& embedding.EntityText == entity.Description
					&& embedding.ModelVersion == fingerprint),
				cancellationToken);

		EmbeddingCoverage coverage = new(
			fingerprint,
			canonicalReady,
			canonicalTotal,
			itemReady,
			itemTotal);
		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken);
		}

		return coverage;
	}

	// Flat shape so the projection stays translatable — EF cannot project into a type with a
	// non-default constructor's worth of nested objects. ToDetail() rebuilds the domain model.
	private sealed class DetailRow
	{
		public Guid Id { get; init; }
		public string CanonicalName { get; init; } = string.Empty;
		public string? DisplayLabel { get; init; }
		public NormalizedDescriptionStatus Status { get; init; }
		public DateTimeOffset CreatedAt { get; init; }
		public Guid? NearestNeighbourId { get; init; }
		public double? NearestNeighbourSimilarity { get; init; }
		public string? NearestNeighbourName { get; init; }
		public int LinkedItemCount { get; init; }
		public DateOnly? LastSeen { get; init; }
		public Guid? LinkedTemplateId { get; init; }
		public string? LinkedTemplateName { get; init; }
		public int LinkedTemplateCount { get; init; }
		public List<string> SampleRawDescriptions { get; set; } = [];

		public NormalizedDescriptionDetail ToDetail() => new(
			new NormalizedDescription(Id, CanonicalName, Status, CreatedAt, NearestNeighbourId, NearestNeighbourSimilarity, DisplayLabel),
			LinkedItemCount,
			NearestNeighbourName,
			SampleRawDescriptions,
			// Midnight UTC, matching how the spending report widens a receipt's DateOnly
			// (ReportService.ToDateTimeOffset). A receipt has a date, not a time.
			LastSeen is null
				? null
				: new DateTimeOffset(LastSeen.Value.Year, LastSeen.Value.Month, LastSeen.Value.Day, 0, 0, 0, TimeSpan.Zero),
			LinkedTemplateId,
			LinkedTemplateName,
			LinkedTemplateCount);
	}

	private static async Task<NormalizedDescriptionEntity?> FindExactCaseInsensitiveAsync(
		ApplicationDbContext context, string canonicalName, CancellationToken cancellationToken)
	{
		// ToLower() translates to LOWER() on both PostgreSQL and InMemory. Paired with the
		// unique functional index on lower("CanonicalName") in the migration, this avoids
		// a sequential scan on Postgres while still working under InMemory for tests.
		string lowered = canonicalName.ToLowerInvariant();
		return await context.NormalizedDescriptions
			.FirstOrDefaultAsync(
				e => e.CanonicalName.ToLower() == lowered,
				cancellationToken);
	}

	// Returns the row plus whether it was actually inserted. Callers that only need the row ignore
	// the flag; SplitAsync needs it, because every early return below hands back a PRE-EXISTING row
	// and reporting that as a freshly split-out description would be a false audit record
	// (RECEIPTS-890).
	private async Task<(NormalizedDescriptionEntity Entity, bool WasInserted)> InsertAsync(
		ApplicationDbContext context,
		string canonicalName,
		NormalizedDescriptionStatus status,
		Vector? embedding,
		CancellationToken cancellationToken,
		Guid? nearestNeighbourId = null,
		double? nearestNeighbourSimilarity = null)
	{
		// Double-check for a race: between the caller's exact-match lookup and this insert,
		// another request may have created a row with the same canonical name. The DB has a
		// unique functional index on lower(CanonicalName), so a second lookup inside this
		// save path gives us a race-safe compromise without needing a distributed lock.
		//
		// Both race paths (here and in the DbUpdateException handler below) return the winning row
		// untouched. We deliberately do not overwrite its near-miss evidence with ours: the winner
		// recorded the neighbour it actually compared against, and clobbering that would replace a
		// true observation with one made against a different candidate set.
		NormalizedDescriptionEntity? preInsert = await FindExactCaseInsensitiveAsync(context, canonicalName, cancellationToken);
		if (preInsert is not null)
		{
			return (preInsert, false);
		}

		NormalizedDescriptionEntity entity = new()
		{
			Id = Guid.NewGuid(),
			CanonicalName = canonicalName,
			Status = status,
			Embedding = embedding,
			EmbeddingModelVersion = embedding is null ? null : OnnxEmbeddingService.EmbeddingSpaceFingerprint,
			CreatedAt = DateTimeOffset.UtcNow,
			NearestNeighbourId = nearestNeighbourId,
			NearestNeighbourSimilarity = nearestNeighbourSimilarity,
		};

		context.NormalizedDescriptions.Add(entity);
		try
		{
			await context.SaveChangesAsync(cancellationToken);
			await committedChangePublisher.PublishAsync(new(
				CommittedEntityType.NormalizedDescription,
				CommittedChangeType.Created,
				entity.Id));
		}
		catch (DbUpdateException)
		{
			// Two different constraints can land us here, and they need opposite responses.
			context.Entry(entity).State = EntityState.Detached;

			// 1. Another writer raced us to the unique functional index on lower(CanonicalName).
			//    Reload the winner and return it.
			NormalizedDescriptionEntity? winner = await FindExactCaseInsensitiveAsync(context, canonicalName, cancellationToken);
			if (winner is not null)
			{
				return (winner, false);
			}

			// 2. No name collision, so the other candidate is the self-FK added in RECEIPTS-873:
			//    MergeAsync can delete our nearest neighbour in the window between the ANN search
			//    and this insert, leaving NearestNeighbourId pointing at a row that no longer
			//    exists. The near-miss is evidence, not essential data — dropping it degrades the
			//    row to "no comparison recorded", which is exactly what ON DELETE SET NULL would
			//    have produced a moment later anyway. Failing the whole resolution over a missing
			//    citation would be the worse trade.
			//
			//    The retry passes no neighbour, so it can only reach the rethrow above — there is
			//    no unbounded recursion here.
			if (nearestNeighbourId is null)
			{
				throw;
			}

			return await InsertAsync(context, canonicalName, status, embedding, cancellationToken);
		}

		return (entity, true);
	}

	private async Task<float[]?> TryGenerateEmbeddingAsync(
		string text,
		CancellationToken cancellationToken,
		bool background = false,
		bool fallbackWhenFull = true)
	{
		try
		{
			if (background && embeddingService is IBackgroundEmbeddingService backgroundEmbeddingService)
			{
				return await backgroundEmbeddingService.GenerateBackgroundEmbeddingAsync(text, cancellationToken);
			}

			return await embeddingService.GenerateEmbeddingAsync(text, cancellationToken);
		}
		catch (EmbeddingQueueFullException) when (fallbackWhenFull)
		{
			// Embeddings enrich these workflows but are not required for correctness. Under
			// deliberate scheduler backpressure, use each caller's existing no-vector fallback.
			return null;
		}
	}
}
