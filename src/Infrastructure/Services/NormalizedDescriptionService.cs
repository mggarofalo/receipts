using System.Security.Cryptography;
using System.Text;
using Application.Interfaces.Services;
using Application.Models;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;
using Infrastructure.Configurations;
using Infrastructure.Entities.Audit;
using Infrastructure.Entities.Core;
using Infrastructure.Mapping;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Infrastructure.Services;

public class NormalizedDescriptionService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	IEmbeddingService embeddingService,
	NormalizedDescriptionMapper mapper,
	NormalizedDescriptionSettingsMapper settingsMapper) : INormalizedDescriptionService
{
	// The thresholds used when no settings row exists yet. These match the migration seed so
	// that pre-migration code paths (tests that don't seed, integration tests spinning up a
	// fresh schema) still see the same decision boundaries as production would at rest.
	public const double InitialAutoAcceptThreshold = NormalizedDescriptionSettingsEntityConfiguration.InitialAutoAcceptThreshold;
	public const double InitialPendingReviewThreshold = NormalizedDescriptionSettingsEntityConfiguration.InitialPendingReviewThreshold;

	public const string ReceiptItemNotFound = "Receipt item not found.";
	public const string SettingsRowNotFound = "NormalizedDescriptionSettings singleton row is missing.";
	public const string TestMatchDescriptionRequired = "Test match description must not be empty.";
	public const string TopNOutOfRange = "topN must be between 1 and 20.";

	private const int MaxTopN = 20;
	private const string PostgreSQL = "Npgsql.EntityFrameworkCore.PostgreSQL";

	// Matches the name CollectAuditEntries derives by stripping the "Entity" suffix, so explicit
	// entries land in the same EntityType bucket as the automatic ones (RECEIPTS-890).
	internal const string NormalizedDescriptionEntityType = "NormalizedDescription";

	// How many distinct raw receipt-item descriptions to surface per canonical row. Enough for a
	// reviewer to recognise what the entry actually covers without turning the queue into a dump
	// of every line item.
	internal const int MaxSampleRawDescriptions = 3;

	public async Task<GetOrCreateResult> GetOrCreateAsync(string rawDescription, CancellationToken cancellationToken)
	{
		string normalized = (rawDescription ?? string.Empty).Trim();
		if (string.IsNullOrEmpty(normalized))
		{
			throw new ArgumentException(NormalizedDescription.CanonicalNameCannotBeEmpty, nameof(rawDescription));
		}

		using ApplicationDbContext context = contextFactory.CreateDbContext();

		// Read thresholds fresh from the DB each call. Call frequency is bounded by the
		// resolver's 30-second poll cycle, so the latency cost is negligible and admin
		// updates take effect on the next run without any cache-invalidation plumbing.
		(double autoAccept, double pendingReview) = await ResolveThresholdsAsync(context, cancellationToken);

		// Step 1: exact case-insensitive match on existing canonical name.
		NormalizedDescriptionEntity? existing = await FindExactCaseInsensitiveAsync(context, normalized, cancellationToken);
		if (existing is not null)
		{
			// A tombstone (RECEIPTS-876). The reviewer already said this text does not deserve a
			// canonical entry, so we hand the row back with its Rejected status intact and let the
			// caller decline to link. Returning it rather than null is deliberate: the caller can
			// then tell "deliberately rejected" from "nothing happened", and log accordingly.
			//
			// MatchScore stays null. A score of 1.0 would be recorded on a ReceiptItem that is not
			// being linked to anything, reproducing exactly the orphan-score inconsistency
			// RECEIPTS-883 exists to prevent.
			if (existing.Status == NormalizedDescriptionStatus.Rejected)
			{
				return new GetOrCreateResult(mapper.ToDomain(existing), MatchScore: null);
			}

			// An exact-name match is a perfect logical match — surface similarity = 1 so
			// the resolver can record it on the ReceiptItem without requiring a second
			// embedding roundtrip.
			return new GetOrCreateResult(mapper.ToDomain(existing), MatchScore: 1.0);
		}

		// Step 2: no embedding capability — create Active entry directly with no vector.
		if (!embeddingService.IsConfigured)
		{
			(NormalizedDescriptionEntity created, _) = await InsertAsync(context, normalized, NormalizedDescriptionStatus.Active, embedding: null, cancellationToken);
			return new GetOrCreateResult(mapper.ToDomain(created), MatchScore: null);
		}

		// Step 3: generate embedding for the input.
		float[] embeddingData = await embeddingService.GenerateEmbeddingAsync(normalized, cancellationToken);
		Vector? embeddingVector = embeddingData.Length > 0 ? new Vector(embeddingData) : null;

		// Step 4: ANN top-1 search — only supported on Postgres. On other providers (InMemory tests)
		// the method is a no-op by default; tests can override AnnSearchTopOneAsync to simulate
		// specific top-1 matches and exercise each threshold band.
		double? topSimilarity = null;
		NormalizedDescriptionEntity? topMatch = null;
		if (embeddingVector is not null)
		{
			(topMatch, topSimilarity) = await AnnSearchTopOneAsync(context, embeddingVector, cancellationToken);
		}

		if (topMatch is not null && topSimilarity.HasValue)
		{
			if (topSimilarity.Value >= autoAccept)
			{
				return new GetOrCreateResult(mapper.ToDomain(topMatch), topSimilarity.Value);
			}

			if (topSimilarity.Value >= pendingReview)
			{
				// Persist the near-miss that caused the pending status (RECEIPTS-873). Previously
				// only the score survived — on the ReceiptItem — so the API could not answer
				// "what did this nearly match?" without recomputing embeddings.
				(NormalizedDescriptionEntity pending, _) = await InsertAsync(
					context,
					normalized,
					NormalizedDescriptionStatus.PendingReview,
					embeddingVector,
					cancellationToken,
					nearestNeighbourId: topMatch.Id,
					nearestNeighbourSimilarity: topSimilarity.Value);
				return new GetOrCreateResult(mapper.ToDomain(pending), topSimilarity.Value);
			}
		}

		(NormalizedDescriptionEntity activeCreated, _) = await InsertAsync(context, normalized, NormalizedDescriptionStatus.Active, embeddingVector, cancellationToken);
		return new GetOrCreateResult(mapper.ToDomain(activeCreated), MatchScore: null);
	}

	/// <summary>
	/// The canonical entry for a user-declared item template (RECEIPTS-881).
	/// </summary>
	/// <remarks>
	/// Deliberately not a call into <see cref="GetOrCreateAsync"/>. That method answers "what does
	/// this receipt text probably mean?" — it runs an ANN search and can land the result in the
	/// review queue. This one records a declaration a user already made by creating the template,
	/// so it takes the exact-match-or-create path only and always produces an <c>Active</c> row.
	/// Routing templates through the resolver would ask a human to confirm a grouping the same
	/// human just made by hand, which is the duplication this issue exists to remove.
	///
	/// The embedding is still generated, because the entry has to be findable by ANN search when
	/// the *same item typed freehand* comes in on a later receipt. That is the whole point: the
	/// template teaches the registry a name, and the registry then recognises it without the
	/// template.
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
			}
			// A PendingReview row is left pending-but-linked rather than auto-approved. The
			// template says what the item is called; it does not say the resolver's *grouping* of
			// whatever raw text landed there is right, and that grouping is what review is for.

			return mapper.ToDomain(existing);
		}

		Vector? embedding = null;
		if (embeddingService.IsConfigured)
		{
			float[] data = await embeddingService.GenerateEmbeddingAsync(normalized, cancellationToken);
			embedding = data.Length > 0 ? new Vector(data) : null;
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
		return await ProjectDetails(context)
			.FirstOrDefaultAsync(d => d.Id == id, cancellationToken) is { } row
			? row.ToDetail()
			: null;
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

		using ApplicationDbContext context = contextFactory.CreateDbContext();

		NormalizedDescriptionEntity? keep = await context.NormalizedDescriptions
			.FirstOrDefaultAsync(e => e.Id == keepId, cancellationToken);
		NormalizedDescriptionEntity? discard = await context.NormalizedDescriptions
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

		// Re-link EVERY ReceiptItem pointing at discard — including soft-deleted (trashed) ones,
		// hence IgnoreQueryFilters. The ReceiptItem -> NormalizedDescription FK is
		// DeleteBehavior.SetNull, so any row still pointing at discard when it is removed below
		// has its link silently nulled by the database; a trashed item would then come back from
		// the recycle bin unlinked, with no error raised anywhere. Same class of bug as the
		// soft-deleted-transaction stranding fixed in AccountMergeService (RECEIPTS-801).
		List<ReceiptItemEntity> items = await context.ReceiptItems
			.IgnoreQueryFilters()
			.IgnoreAutoIncludes()
			.Where(r => r.NormalizedDescriptionId == discardId)
			.ToListAsync(cancellationToken);

		// Repoint AND rescore. The score on each item was the cosine similarity to the
		// DISCARDED row's embedding, so once the item points at `keep` it describes a comparison
		// that no longer exists. That is not cosmetic: PreviewThresholdImpactAsync buckets items
		// by exactly this column, so every threshold-impact preview run after a merge would be
		// computed partly from scores measured against a row that has been deleted.
		//
		// Leaving the score null instead is not an option here. The resolver only picks up rows
		// WHERE "NormalizedDescriptionId" IS NULL, and a merged item keeps its link — so nothing
		// would ever repopulate it, and the item would count as structurally unresolved forever.
		//
		// Grouped by description so one embedding serves every item sharing that text, mirroring
		// the resolver's per-cycle grouping. Merges are low-volume admin operations, so the cost
		// is a handful of embeddings per merge at worst.
		int rescoredCount = 0;
		foreach (IGrouping<string, ReceiptItemEntity> group in items.GroupBy(i => i.Description, StringComparer.Ordinal))
		{
			double? similarity = await SimilarityToKeepAsync(context, group.Key, keep, cancellationToken);

			foreach (ReceiptItemEntity item in group)
			{
				item.NormalizedDescriptionId = keepId;
				if (item.NormalizedDescriptionMatchScore != similarity)
				{
					item.NormalizedDescriptionMatchScore = similarity;
					if (item.DeletedAt is null)
					{
						rescoredCount++;
					}
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
				new FieldChange { FieldName = "rescoredItemCount", OldValue = null, NewValue = rescoredCount.ToString() },
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
				new FieldChange { FieldName = "rescoredItemCount", OldValue = null, NewValue = rescoredCount.ToString() },
				new FieldChange { FieldName = "relinkedTemplateCount", OldValue = null, NewValue = templates.Count.ToString() },
			],
			now);

		await context.SaveChangesAsync(cancellationToken);

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

		// Match the normalization contract from GetOrCreateAsync so that callers can't create
		// whitespace-divergent duplicates via Split.
		string canonicalName = (name ?? string.Empty).Trim();
		if (string.IsNullOrEmpty(canonicalName))
		{
			throw new ArgumentException(NormalizedDescription.CanonicalNameCannotBeEmpty, nameof(name));
		}

		using ApplicationDbContext context = contextFactory.CreateDbContext();

		List<Guid> distinctIds = [.. receiptItemIds.Distinct()];
		List<ReceiptItemEntity> items = await context.ReceiptItems
			.IgnoreAutoIncludes()
			.Where(r => distinctIds.Contains(r.Id))
			.ToListAsync(cancellationToken);

		if (items.Count != distinctIds.Count)
		{
			throw new KeyNotFoundException(ReceiptItemNotFound);
		}

		ReceiptItemEntity item = items[0];

		// Generate an embedding for the split item's raw description if possible, so the
		// new entry is consistent with entries produced by GetOrCreateAsync.
		Vector? embeddingVector = null;
		if (embeddingService.IsConfigured)
		{
			float[] data = await embeddingService.GenerateEmbeddingAsync(canonicalName, cancellationToken);
			if (data.Length > 0)
			{
				embeddingVector = new Vector(data);
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

		(NormalizedDescriptionEntity created, bool wasInserted) = await InsertAsync(
			context,
			canonicalName,
			NormalizedDescriptionStatus.Active,
			embeddingVector,
			cancellationToken);

		// Repoint AND rescore, for the reason MergeAsync does (RECEIPTS-892): each item's score was
		// the similarity to the row it is leaving, so once it points at `created` the number
		// describes a comparison that no longer applies. PreviewThresholdImpactAsync buckets items
		// by exactly this column, so a stale score would skew every later threshold preview.
		//
		// Grouped by description so one embedding serves every item sharing that text.
		foreach (IGrouping<string, ReceiptItemEntity> group in items.GroupBy(r => r.Description, StringComparer.Ordinal))
		{
			double? similarity = await SimilarityToKeepAsync(context, group.Key, created, cancellationToken);
			foreach (ReceiptItemEntity moved in group)
			{
				moved.NormalizedDescriptionId = created.Id;
				moved.NormalizedDescriptionMatchScore = similarity;
			}
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

		await context.SaveChangesAsync(cancellationToken);

		// Re-read through the same projection the list endpoint uses so the caller gets a truthful
		// LinkedItemCount for the row it just created, rather than a count derived from the
		// selection that would drift the moment Split's semantics change. One extra query on an
		// admin-only action.
		DetailRow? row = await ProjectDetails(context)
			.FirstOrDefaultAsync(d => d.Id == created.Id, cancellationToken);

		// The row was just committed in this same context, so a miss here means something deleted
		// it out from under us mid-call. Fall back to the in-memory entity with the evidence we
		// know first-hand rather than throwing.
		return row?.ToDetail()
			?? new NormalizedDescriptionDetail(mapper.ToDomain(created), items.Count, NearestNeighbourName: null, [canonicalName]);
	}

	public async Task<bool> UpdateStatusAsync(Guid id, NormalizedDescriptionStatus status, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		NormalizedDescriptionEntity? entity = await context.NormalizedDescriptions
			.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
		if (entity is null || entity.Status == status)
		{
			return false;
		}

		NormalizedDescriptionStatus previous = entity.Status;
		entity.Status = status;

		if (status == NormalizedDescriptionStatus.Rejected)
		{
			await DetachItemsForRejectionAsync(context, entity, previous, cancellationToken);
		}

		await context.SaveChangesAsync(cancellationToken);
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
		List<ReceiptItemEntity> items = await context.ReceiptItems
			.IgnoreQueryFilters()
			.IgnoreAutoIncludes()
			.Where(r => r.NormalizedDescriptionId == entity.Id)
			.ToListAsync(cancellationToken);

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
		List<ItemTemplateEntity> templates = await context.ItemTemplates
			.IgnoreQueryFilters()
			.Where(t => t.NormalizedDescriptionId == entity.Id)
			.ToListAsync(cancellationToken);

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

		// A label that merely restates the row's own matched text is stored as null rather than a
		// duplicate copy. Otherwise the row would be permanently "renamed" to the thing it already
		// displayed, and a later edit to CanonicalName-derived display logic would not reach it.
		if (trimmed is not null && string.Equals(trimmed, entity.CanonicalName, StringComparison.OrdinalIgnoreCase))
		{
			trimmed = null;
		}

		// Checked here as well as by the unique index so the caller gets a usable message instead
		// of a raw constraint violation. The index remains the authority under concurrency — see
		// the DbUpdateException handler below.
		if (trimmed is not null && await DisplayNameTakenAsync(context, trimmed, id, cancellationToken))
		{
			throw new InvalidOperationException(DisplayNameAlreadyTaken);
		}

		entity.DisplayLabel = trimmed;

		try
		{
			await context.SaveChangesAsync(cancellationToken);
		}
		catch (DbUpdateException)
		{
			// Another writer claimed the same display name between the check above and this save.
			throw new InvalidOperationException(DisplayNameAlreadyTaken);
		}

		DetailRow? row = await ProjectDetails(context)
			.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

		return row?.ToDetail()
			?? new NormalizedDescriptionDetail(mapper.ToDomain(entity), LinkedItemCount: 0, NearestNeighbourName: null, []);
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
		// text was not worth an entry, and the resolver would be free to recreate it on the next
		// receipt.
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
		// (the embedding service is down mid-rescore, the caller goes away) would leave the template
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

	public async Task<NormalizedDescriptionSettings> GetSettingsAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		NormalizedDescriptionSettingsEntity entity = await ResolveSettingsEntityAsync(context, cancellationToken);
		return settingsMapper.ToDomain(entity);
	}

	public async Task<NormalizedDescriptionSettings> UpdateSettingsAsync(
		double autoAcceptThreshold,
		double pendingReviewThreshold,
		CancellationToken cancellationToken)
	{
		// Domain-level validation: prevents malformed bounds (out-of-range, crossed thresholds)
		// from ever hitting the DB. Mirrors the constructor on NormalizedDescriptionSettings.
		NormalizedDescriptionSettings.Validate(autoAcceptThreshold, pendingReviewThreshold);

		using ApplicationDbContext context = contextFactory.CreateDbContext();
		NormalizedDescriptionSettingsEntity entity = await ResolveSettingsEntityAsync(context, cancellationToken);

		entity.AutoAcceptThreshold = autoAcceptThreshold;
		entity.PendingReviewThreshold = pendingReviewThreshold;
		entity.UpdatedAt = DateTimeOffset.UtcNow;

		await context.SaveChangesAsync(cancellationToken);
		return settingsMapper.ToDomain(entity);
	}

	public async Task<MatchTestResult> TestMatchAsync(
		string description,
		int topN,
		double? autoAcceptThresholdOverride,
		double? pendingReviewThresholdOverride,
		CancellationToken cancellationToken)
	{
		string normalized = (description ?? string.Empty).Trim();
		if (string.IsNullOrEmpty(normalized))
		{
			throw new ArgumentException(TestMatchDescriptionRequired, nameof(description));
		}

		if (topN < 1 || topN > MaxTopN)
		{
			throw new ArgumentException(TopNOutOfRange, nameof(topN));
		}

		// Validate any thresholds the admin supplied. We accept partial overrides (one or the
		// other) but still need the combined pair to satisfy the invariant: fall back to the
		// DB values for the unset side, then validate the resulting pair.
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		NormalizedDescriptionSettingsEntity settings = await ResolveSettingsEntityAsync(context, cancellationToken);

		double autoAccept = autoAcceptThresholdOverride ?? settings.AutoAcceptThreshold;
		double pendingReview = pendingReviewThresholdOverride ?? settings.PendingReviewThreshold;
		NormalizedDescriptionSettings.Validate(autoAccept, pendingReview);

		// If there is an exact case-insensitive match, the resolver would short-circuit
		// without ever querying embeddings. Mirror that here so the preview is truthful.
		NormalizedDescriptionEntity? exactMatch = await FindExactCaseInsensitiveAsync(context, normalized, cancellationToken);
		if (exactMatch is not null)
		{
			List<MatchCandidate> exactCandidates =
			[
				new MatchCandidate(
					exactMatch.Id,
					exactMatch.CanonicalName,
					1.0,
					exactMatch.Status.ToString()),
			];
			return new MatchTestResult(exactCandidates, MatchTestOutcomes.AutoAccept, exactMatch.Id);
		}

		if (!embeddingService.IsConfigured)
		{
			// No embedding service — the real resolver would create a new Active entry, but
			// admins still deserve an honest answer: no candidates, and a dedicated outcome
			// so the UI can surface a banner. SimulatedTargetId is null because the new
			// entry doesn't exist yet.
			return new MatchTestResult([], MatchTestOutcomes.EmbeddingUnavailable, SimulatedTargetId: null);
		}

		float[] embeddingData = await embeddingService.GenerateEmbeddingAsync(normalized, cancellationToken);
		if (embeddingData.Length == 0)
		{
			return new MatchTestResult([], MatchTestOutcomes.EmbeddingUnavailable, SimulatedTargetId: null);
		}

		Vector queryVector = new(embeddingData);
		List<MatchCandidate> candidates = await AnnSearchTopNAsync(context, queryVector, topN, cancellationToken);

		// The resolver makes its branch decision against the top-1 candidate, so we do too —
		// the rest of the candidates are informational only.
		MatchCandidate? top = candidates.Count > 0 ? candidates[0] : null;
		if (top is not null)
		{
			if (top.CosineSimilarity >= autoAccept)
			{
				return new MatchTestResult(candidates, MatchTestOutcomes.AutoAccept, top.NormalizedDescriptionId);
			}

			if (top.CosineSimilarity >= pendingReview)
			{
				// In the real resolver this would create a new PendingReview entry linked in
				// a neighbourhood of `top`; SimulatedTargetId=null because the row doesn't
				// exist yet. The caller has the top candidate in `candidates[0]` for context.
				return new MatchTestResult(candidates, MatchTestOutcomes.PendingReview, SimulatedTargetId: null);
			}
		}

		return new MatchTestResult(candidates, MatchTestOutcomes.CreateNew, SimulatedTargetId: null);
	}

	public async Task<ThresholdImpactPreview> PreviewThresholdImpactAsync(
		double autoAcceptThreshold,
		double pendingReviewThreshold,
		CancellationToken cancellationToken)
	{
		NormalizedDescriptionSettings.Validate(autoAcceptThreshold, pendingReviewThreshold);

		using ApplicationDbContext context = contextFactory.CreateDbContext();
		NormalizedDescriptionSettingsEntity settings = await ResolveSettingsEntityAsync(context, cancellationToken);

		// Snapshot the scored live-set once. We bucketise twice (current thresholds and
		// proposed thresholds) over the same in-memory list to keep the two classifications
		// strictly comparable — querying twice would risk a race if items were resolved
		// between the two counts. An item only enters this list if BOTH the FK and the
		// score are populated; otherwise it's structurally unresolved and no threshold
		// change can reclassify it. The set is bounded (only resolved items) so memory
		// cost is modest relative to the total ReceiptItems table.
		List<double> scored = await context.ReceiptItems
			.AsNoTracking()
			.IgnoreAutoIncludes()
			.Where(r => r.NormalizedDescriptionMatchScore != null && r.NormalizedDescriptionId != null)
			.Select(r => r.NormalizedDescriptionMatchScore!.Value)
			.ToListAsync(cancellationToken);

		// Items without a match score or without any linked NormalizedDescription are
		// counted as structurally unresolved regardless of threshold choice.
		int unresolvedCount = await context.ReceiptItems
			.AsNoTracking()
			.IgnoreAutoIncludes()
			.CountAsync(r => r.NormalizedDescriptionMatchScore == null || r.NormalizedDescriptionId == null, cancellationToken);

		ClassificationCounts current = Classify(scored, settings.AutoAcceptThreshold, settings.PendingReviewThreshold, unresolvedCount);
		ClassificationCounts proposed = Classify(scored, autoAcceptThreshold, pendingReviewThreshold, unresolvedCount);

		// Deltas: per-item transitions between the two classification maps. We don't have
		// item identity here, just a list of scores — so we compute counts by bucket
		// intersection (e.g., items currently auto-accepted but proposed-pending-review =
		// scores where current.auto holds but proposed.auto doesn't and proposed.pending
		// does). Same for Unresolved → {auto, pending}, which falls out of the score nulls:
		// null-score items never change bucket, so Unresolved→X transitions only apply to
		// the "scored but currently below pendingReview" sub-slice.
		int autoToPending = scored.Count(s =>
			s >= settings.AutoAcceptThreshold &&
			s >= pendingReviewThreshold && s < autoAcceptThreshold);

		int pendingToAuto = scored.Count(s =>
			s >= settings.PendingReviewThreshold && s < settings.AutoAcceptThreshold &&
			s >= autoAcceptThreshold);

		// Unresolved→X deltas describe currently-unresolved-by-threshold items (i.e., scored
		// but below the current pending-review floor) that would move up under the proposal.
		// NULL-score items are "structurally unresolved" and cannot move via a threshold change.
		int unresolvedToAuto = scored.Count(s =>
			s < settings.PendingReviewThreshold &&
			s >= autoAcceptThreshold);

		int unresolvedToPending = scored.Count(s =>
			s < settings.PendingReviewThreshold &&
			s >= pendingReviewThreshold && s < autoAcceptThreshold);

		ReclassificationDeltas deltas = new(autoToPending, pendingToAuto, unresolvedToAuto, unresolvedToPending);
		return new ThresholdImpactPreview(current, proposed, deltas);
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

		// The resolver drains unresolved items at BatchSize per Interval. Approximate on purpose:
		// the batch is shared with any items that were already unresolved before the requeue, so
		// this is a floor on the catch-up time, not a promise.
		int cycles = (int)Math.Ceiling(
			linkedItemCount / (double)NormalizedDescriptionResolutionService.BatchSize);
		int seconds = cycles * (int)NormalizedDescriptionResolutionService.Interval.TotalSeconds;

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

	private static ClassificationCounts Classify(
		List<double> scored,
		double autoAcceptThreshold,
		double pendingReviewThreshold,
		int unresolvedCount)
	{
		int autoAccepted = 0;
		int pendingReview = 0;
		int belowFloor = 0;
		foreach (double score in scored)
		{
			if (score >= autoAcceptThreshold)
			{
				autoAccepted++;
			}
			else if (score >= pendingReviewThreshold)
			{
				pendingReview++;
			}
			else
			{
				belowFloor++;
			}
		}

		// Unresolved = structurally-unresolved (NULL score) + "scored but below pending-review"
		// (i.e., the resolver would have created a new canonical entry, so they're still
		// effectively unresolved against any existing NormalizedDescription).
		return new ClassificationCounts(autoAccepted, pendingReview, unresolvedCount + belowFloor);
	}

	private async Task<(double AutoAccept, double PendingReview)> ResolveThresholdsAsync(
		ApplicationDbContext context,
		CancellationToken cancellationToken)
	{
		NormalizedDescriptionSettingsEntity? entity = await context.NormalizedDescriptionSettings
			.AsNoTracking()
			.FirstOrDefaultAsync(e => e.Id == NormalizedDescriptionSettingsEntityConfiguration.SingletonId, cancellationToken);

		if (entity is null)
		{
			// Fallback path for contexts that haven't been seeded (unit tests using a fresh
			// InMemory provider, integration harnesses that skip EF migrations). The initial
			// constants mirror the seed row so behaviour is identical at rest.
			return (InitialAutoAcceptThreshold, InitialPendingReviewThreshold);
		}

		return (entity.AutoAcceptThreshold, entity.PendingReviewThreshold);
	}

	private async Task<NormalizedDescriptionSettingsEntity> ResolveSettingsEntityAsync(
		ApplicationDbContext context,
		CancellationToken cancellationToken)
	{
		NormalizedDescriptionSettingsEntity? entity = await context.NormalizedDescriptionSettings
			.FirstOrDefaultAsync(e => e.Id == NormalizedDescriptionSettingsEntityConfiguration.SingletonId, cancellationToken);

		if (entity is not null)
		{
			return entity;
		}

		// Self-heal path: if the seed row is missing (e.g., migrations were rolled back and
		// re-applied in a narrow window, or an InMemory test skipped seeding) we bootstrap
		// the singleton with defaults on first read/write rather than failing loudly. The
		// fixed SingletonId plus PK means the insert is race-safe: a second concurrent call
		// would hit a PK violation and reload.
		entity = new NormalizedDescriptionSettingsEntity
		{
			Id = NormalizedDescriptionSettingsEntityConfiguration.SingletonId,
			AutoAcceptThreshold = InitialAutoAcceptThreshold,
			PendingReviewThreshold = InitialPendingReviewThreshold,
			UpdatedAt = DateTimeOffset.UtcNow,
		};
		context.NormalizedDescriptionSettings.Add(entity);
		try
		{
			await context.SaveChangesAsync(cancellationToken);
		}
		catch (DbUpdateException)
		{
			context.Entry(entity).State = EntityState.Detached;
			NormalizedDescriptionSettingsEntity? winner = await context.NormalizedDescriptionSettings
				.FirstOrDefaultAsync(e => e.Id == NormalizedDescriptionSettingsEntityConfiguration.SingletonId, cancellationToken);
			if (winner is null)
			{
				throw new InvalidOperationException(SettingsRowNotFound);
			}

			return winner;
		}

		return entity;
	}

	// Single-query evidence projection shared by GetAllAsync / GetByIdAsync / SplitAsync
	// (RECEIPTS-873). Three things happen here that a naive implementation would get wrong:
	//
	//  1. No N+1. LinkedItemCount and SampleRawDescriptions are correlated subqueries, which EF
	//     translates to lateral joins on Npgsql — one round trip regardless of row count. The
	//     existing IX_ReceiptItems_NormalizedDescriptionId index covers both.
	//  2. Soft-deleted receipt items are excluded automatically: the ReceiptItemEntity query filter
	//     applies inside subqueries, so a deleted item never inflates the count an admin sees.
	//  3. Embedding is not selected. The old GetAllAsync materialized whole entities, dragging a
	//     384-float vector per row across the wire for a list that never displays it.
	//
	// Samples are ordered before Take so the same rows produce the same samples across calls —
	// an unordered LIMIT would let the displayed evidence shuffle between refreshes.
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
				SampleRawDescriptions = context.ReceiptItems
					.Where(r => r.NormalizedDescriptionId == e.Id)
					.Select(r => r.Description)
					.Distinct()
					.OrderBy(d => d)
					.Take(MaxSampleRawDescriptions)
					.ToList(),
			});

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
		public List<string> SampleRawDescriptions { get; init; } = [];

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
			EmbeddingModelVersion = embedding is null ? null : OnnxEmbeddingService.ModelName,
			CreatedAt = DateTimeOffset.UtcNow,
			NearestNeighbourId = nearestNeighbourId,
			NearestNeighbourSimilarity = nearestNeighbourSimilarity,
		};

		context.NormalizedDescriptions.Add(entity);
		try
		{
			await context.SaveChangesAsync(cancellationToken);
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

	/// <summary>
	/// Cosine similarity between <paramref name="rawDescription"/> and the surviving row's
	/// embedding, for re-scoring items re-linked by a merge (RECEIPTS-892).
	/// </summary>
	/// <remarks>
	/// Returns null when no honest number can be produced — no embedding service, the surviving
	/// row has no vector, or the provider is not Postgres. A null score is the correct answer
	/// there: it reads as "unresolved" rather than asserting a similarity nobody measured.
	/// </remarks>
	private async Task<double?> SimilarityToKeepAsync(
		ApplicationDbContext context,
		string rawDescription,
		NormalizedDescriptionEntity keep,
		CancellationToken cancellationToken)
	{
		string normalized = (rawDescription ?? string.Empty).Trim();
		if (string.IsNullOrEmpty(normalized))
		{
			return null;
		}

		// An exact case-insensitive name match is a perfect logical match. GetOrCreateAsync
		// short-circuits to 1.0 here rather than paying for an embedding, and the score this
		// writes has to mean the same thing as the score the resolver would have written.
		if (string.Equals(normalized, keep.CanonicalName, StringComparison.OrdinalIgnoreCase))
		{
			return 1.0;
		}

		if (!embeddingService.IsConfigured || keep.Embedding is null)
		{
			return null;
		}

		float[] embeddingData = await embeddingService.GenerateEmbeddingAsync(normalized, cancellationToken);
		if (embeddingData.Length == 0)
		{
			return null;
		}

		return await SimilarityToAsync(context, new Vector(embeddingData), keep.Id, cancellationToken);
	}

	// Virtual so tests can stub a similarity without pgvector, matching AnnSearchTopOneAsync.
	// On providers that don't support pgvector (e.g., InMemory) the default is a no-op.
	protected virtual async Task<double?> SimilarityToAsync(
		ApplicationDbContext context,
		Vector queryVector,
		Guid targetId,
		CancellationToken cancellationToken)
	{
		if (context.Database.ProviderName != PostgreSQL)
		{
			return null;
		}

		// Same expression as the ANN searches — `<=>` is cosine distance, so 1 - it is the
		// similarity — but pinned to one row rather than ordered over the whole table.
		string sql = """
			SELECT "Id" AS entity_id,
			       (1.0 - ("Embedding" <=> {0}::vector)) AS similarity
			FROM "matching"."NormalizedDescriptions"
			WHERE "Id" = {1} AND "Embedding" IS NOT NULL
			""";

		AnnSearchRow? row = await context.Database
			.SqlQueryRaw<AnnSearchRow>(sql, queryVector, targetId)
			.FirstOrDefaultAsync(cancellationToken);

		return row?.similarity;
	}

	// Virtual so tests can simulate specific top-1 matches without a real Postgres. On providers
	// that don't support pgvector (e.g., InMemory) the default implementation is a no-op.
	protected virtual async Task<(NormalizedDescriptionEntity? Match, double? Similarity)> AnnSearchTopOneAsync(
		ApplicationDbContext context, Vector queryVector, CancellationToken cancellationToken)
	{
		if (context.Database.ProviderName != PostgreSQL)
		{
			return (null, null);
		}

		// pgvector's `<=>` operator returns cosine distance (1 - cosine_similarity).
		// The partial HNSW index covers the WHERE "Embedding" IS NOT NULL clause.
		//
		// Tombstones are excluded (RECEIPTS-876). A Rejected row keeps its embedding so the exact
		// -match lookup still finds it, but it must never win an ANN search: auto-accepting onto a
		// rejected row would re-link the very items the reviewer detached, and citing one as a
		// near-miss would offer "nearly matched <thing you rejected>" as evidence.
		string sql = """
			SELECT "Id" AS entity_id,
			       (1.0 - ("Embedding" <=> {0}::vector)) AS similarity
			FROM "matching"."NormalizedDescriptions"
			WHERE "Embedding" IS NOT NULL AND "Status" <> 'Rejected'
			ORDER BY "Embedding" <=> {0}::vector
			LIMIT 1
			""";

		AnnSearchRow? row = await context.Database
			.SqlQueryRaw<AnnSearchRow>(sql, queryVector)
			.FirstOrDefaultAsync(cancellationToken);

		if (row is null)
		{
			return (null, null);
		}

		NormalizedDescriptionEntity? entity = await context.NormalizedDescriptions
			.FirstOrDefaultAsync(e => e.Id == row.entity_id, cancellationToken);
		return (entity, row.similarity);
	}

	// Virtual so tests can stub an N-row result without pgvector. Callers cap topN at MaxTopN.
	protected virtual async Task<List<MatchCandidate>> AnnSearchTopNAsync(
		ApplicationDbContext context,
		Vector queryVector,
		int topN,
		CancellationToken cancellationToken)
	{
		if (context.Database.ProviderName != PostgreSQL)
		{
			return [];
		}

		// Same index and same tombstone exclusion as AnnSearchTopOneAsync — the settings-page
		// match test must simulate what the resolver would actually do, and the resolver can
		// never land on a Rejected row. Raising LIMIT costs extra index probes but no additional
		// table scans; safe to keep at topN ≤ 20.
		string sql = """
			SELECT "Id" AS entity_id,
			       (1.0 - ("Embedding" <=> {0}::vector)) AS similarity
			FROM "matching"."NormalizedDescriptions"
			WHERE "Embedding" IS NOT NULL AND "Status" <> 'Rejected'
			ORDER BY "Embedding" <=> {0}::vector
			LIMIT {1}
			""";

		List<AnnSearchRow> rows = await context.Database
			.SqlQueryRaw<AnnSearchRow>(sql, queryVector, topN)
			.ToListAsync(cancellationToken);

		if (rows.Count == 0)
		{
			return [];
		}

		List<Guid> ids = [.. rows.Select(r => r.entity_id)];
		Dictionary<Guid, NormalizedDescriptionEntity> entities = await context.NormalizedDescriptions
			.Where(e => ids.Contains(e.Id))
			.ToDictionaryAsync(e => e.Id, cancellationToken);

		List<MatchCandidate> candidates = [];
		foreach (AnnSearchRow row in rows)
		{
			if (!entities.TryGetValue(row.entity_id, out NormalizedDescriptionEntity? entity))
			{
				continue;
			}

			candidates.Add(new MatchCandidate(
				entity.Id,
				entity.CanonicalName,
				row.similarity,
				entity.Status.ToString()));
		}

		return candidates;
	}

	private sealed class AnnSearchRow
	{
#pragma warning disable IDE1006 // Underscore naming matches raw-SQL column aliases.
		public Guid entity_id { get; set; }
		public double similarity { get; set; }
#pragma warning restore IDE1006
	}
}
