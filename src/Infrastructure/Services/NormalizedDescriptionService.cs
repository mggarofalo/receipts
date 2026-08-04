using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;
using Infrastructure.Configurations;
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
			// An exact-name match is a perfect logical match — surface similarity = 1 so
			// the resolver can record it on the ReceiptItem without requiring a second
			// embedding roundtrip.
			return new GetOrCreateResult(mapper.ToDomain(existing), MatchScore: 1.0);
		}

		// Step 2: no embedding capability — create Active entry directly with no vector.
		if (!embeddingService.IsConfigured)
		{
			NormalizedDescriptionEntity created = await InsertAsync(context, normalized, NormalizedDescriptionStatus.Active, embedding: null, cancellationToken);
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
				NormalizedDescriptionEntity pending = await InsertAsync(
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

		NormalizedDescriptionEntity activeCreated = await InsertAsync(context, normalized, NormalizedDescriptionStatus.Active, embeddingVector, cancellationToken);
		return new GetOrCreateResult(mapper.ToDomain(activeCreated), MatchScore: null);
	}

	public async Task<NormalizedDescriptionDetail?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await ProjectDetails(context)
			.FirstOrDefaultAsync(d => d.Id == id, cancellationToken) is { } row
			? row.ToDetail()
			: null;
	}

	public async Task<List<NormalizedDescriptionDetail>> GetAllAsync(NormalizedDescriptionStatus? filter, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<DetailRow> query = ProjectDetails(context);
		if (filter.HasValue)
		{
			query = query.Where(d => d.Status == filter.Value);
		}

		List<DetailRow> rows = await query
			.OrderBy(d => d.CanonicalName)
			.ToListAsync(cancellationToken);
		return [.. rows.Select(r => r.ToDetail())];
	}

	public async Task<int> MergeAsync(Guid keepId, Guid discardId, CancellationToken cancellationToken)
	{
		if (keepId == discardId)
		{
			return 0;
		}

		using ApplicationDbContext context = contextFactory.CreateDbContext();

		NormalizedDescriptionEntity? keep = await context.NormalizedDescriptions
			.FirstOrDefaultAsync(e => e.Id == keepId, cancellationToken);
		NormalizedDescriptionEntity? discard = await context.NormalizedDescriptions
			.FirstOrDefaultAsync(e => e.Id == discardId, cancellationToken);

		if (keep is null || discard is null)
		{
			return 0;
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

		foreach (ReceiptItemEntity item in items)
		{
			item.NormalizedDescriptionId = keepId;
		}

		context.NormalizedDescriptions.Remove(discard);

		await context.SaveChangesAsync(cancellationToken);

		// The returned count keeps its established meaning — live items re-linked — so the
		// admin-facing "N items re-linked" number still matches what a report would show.
		// Trashed rows are repointed for integrity but deliberately not counted.
		return items.Count(item => item.DeletedAt is null);
	}

	public async Task<NormalizedDescriptionDetail> SplitAsync(Guid receiptItemId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();

		ReceiptItemEntity? item = await context.ReceiptItems
			.IgnoreAutoIncludes()
			.FirstOrDefaultAsync(r => r.Id == receiptItemId, cancellationToken);
		if (item is null)
		{
			throw new KeyNotFoundException(ReceiptItemNotFound);
		}

		// Match the normalization contract from GetOrCreateAsync so that callers can't create
		// whitespace-divergent duplicates via Split.
		string canonicalName = (item.Description ?? string.Empty).Trim();
		if (string.IsNullOrEmpty(canonicalName))
		{
			throw new ArgumentException(NormalizedDescription.CanonicalNameCannotBeEmpty, nameof(receiptItemId));
		}

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

		NormalizedDescriptionEntity created = await InsertAsync(
			context,
			canonicalName,
			NormalizedDescriptionStatus.Active,
			embeddingVector,
			cancellationToken);

		item.NormalizedDescriptionId = created.Id;
		await context.SaveChangesAsync(cancellationToken);

		// Re-read through the same projection the list endpoint uses so the caller gets a truthful
		// LinkedItemCount for the row it just created, rather than a hardcoded 1 that would drift
		// the moment Split's semantics change. One extra query on an admin-only action.
		DetailRow? row = await ProjectDetails(context)
			.FirstOrDefaultAsync(d => d.Id == created.Id, cancellationToken);

		// The row was just committed in this same context, so a miss here means something deleted
		// it out from under us mid-call. Fall back to the in-memory entity with the evidence we
		// know first-hand rather than throwing.
		return row?.ToDetail()
			?? new NormalizedDescriptionDetail(mapper.ToDomain(created), LinkedItemCount: 1, NearestNeighbourName: null, [canonicalName]);
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

		entity.Status = status;
		await context.SaveChangesAsync(cancellationToken);
		return true;
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

		int pendingCount = await context.NormalizedDescriptions
			.AsNoTracking()
			.CountAsync(e => e.Status == NormalizedDescriptionStatus.PendingReview, cancellationToken);

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

		return new RequeuePendingPreview(pendingCount, linkedItemCount, staleMatchScoreCount, cycles, seconds);
	}

	public async Task<RequeuePendingResult?> RequeuePendingAsync(int expectedPendingCount, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();

		List<NormalizedDescriptionEntity> pending = await context.NormalizedDescriptions
			.Where(e => e.Status == NormalizedDescriptionStatus.PendingReview)
			.ToListAsync(cancellationToken);

		// Optimistic guard against a stale caller. The admin previewed a specific blast radius and
		// confirmed THAT number; if the resolver has since queued more rows, deleting them silently
		// would destroy review candidates nobody looked at. Bail out and make the caller re-read.
		if (pending.Count != expectedPendingCount)
		{
			return null;
		}

		if (pending.Count == 0)
		{
			// Re-runnable by design: a second pass with nothing pending is a no-op, not an error.
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

		// Single SaveChanges: either the unlink, the score clear and the delete all land, or none do.
		// A partial commit would strand items pointing at deleted rows.
		await context.SaveChangesAsync(cancellationToken);

		return new RequeuePendingResult(pending.Count, unlinkedItemCount, clearedMatchScoreCount);
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
				Status = e.Status,
				CreatedAt = e.CreatedAt,
				NearestNeighbourId = e.NearestNeighbourId,
				NearestNeighbourSimilarity = e.NearestNeighbourSimilarity,
				NearestNeighbourName = e.NearestNeighbour == null ? null : e.NearestNeighbour.CanonicalName,
				LinkedItemCount = context.ReceiptItems.Count(r => r.NormalizedDescriptionId == e.Id),
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
		public NormalizedDescriptionStatus Status { get; init; }
		public DateTimeOffset CreatedAt { get; init; }
		public Guid? NearestNeighbourId { get; init; }
		public double? NearestNeighbourSimilarity { get; init; }
		public string? NearestNeighbourName { get; init; }
		public int LinkedItemCount { get; init; }
		public List<string> SampleRawDescriptions { get; init; } = [];

		public NormalizedDescriptionDetail ToDetail() => new(
			new NormalizedDescription(Id, CanonicalName, Status, CreatedAt, NearestNeighbourId, NearestNeighbourSimilarity),
			LinkedItemCount,
			NearestNeighbourName,
			SampleRawDescriptions);
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

	private async Task<NormalizedDescriptionEntity> InsertAsync(
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
			return preInsert;
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
				return winner;
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

		return entity;
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
		string sql = """
			SELECT "Id" AS entity_id,
			       (1.0 - ("Embedding" <=> {0}::vector)) AS similarity
			FROM "matching"."NormalizedDescriptions"
			WHERE "Embedding" IS NOT NULL
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

		// Same index as AnnSearchTopOneAsync (partial HNSW on Embedding). Raising LIMIT costs
		// extra index probes but no additional table scans; safe to keep at topN ≤ 20.
		string sql = """
			SELECT "Id" AS entity_id,
			       (1.0 - ("Embedding" <=> {0}::vector)) AS similarity
			FROM "matching"."NormalizedDescriptions"
			WHERE "Embedding" IS NOT NULL
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
