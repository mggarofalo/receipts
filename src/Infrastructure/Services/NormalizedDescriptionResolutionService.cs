using System.Threading.Channels;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

// Background resolver that scans unresolved ReceiptItemEntity rows and links each to a
// NormalizedDescription (RECEIPTS-578). Mirrors EmbeddingGenerationService: 10s initial
// delay, 30s poll cycle, 50-row batches. Per cycle we group rows by raw description so the
// same canonical lookup isn't run twice for duplicate text; each unique description hits
// NormalizedDescriptionService.GetOrCreateAsync exactly once and its (Id, MatchScore) is
// written onto every row in the group.
//
// Signal-driven via IDescriptionChangeSignal — a description-change broadcast hints that new
// ReceiptItems may need normalization. Consumers are idempotent. This service Subscribe()s once
// for its OWN wake-up channel so a signal can never be "stolen" by another consumer
// (RECEIPTS-790).
//
// Errors are logged and the cycle retries next tick — we never surface exceptions past the
// hosted-service boundary because a resolver crash would otherwise cascade into the host
// (BackgroundServiceExceptionBehavior.StopHost default).
public class NormalizedDescriptionResolutionService(
	IServiceScopeFactory scopeFactory,
	IDescriptionChangeSignal signal,
	ILogger<NormalizedDescriptionResolutionService> logger,
	ICommittedChangePublisher committedChangePublisher) : BackgroundService
{
	public NormalizedDescriptionResolutionService(
		IServiceScopeFactory scopeFactory,
		IDescriptionChangeSignal signal,
		ILogger<NormalizedDescriptionResolutionService> logger)
		: this(scopeFactory, signal, logger, new NullCommittedChangePublisher())
	{
	}

	internal const int BatchSize = 50;
	internal const int MinDescriptionLength = 2;
	internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
	internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

	// Subscribed at construction (before any NotifyDirty this instance should react to) so a
	// wake-up fired during startup is buffered rather than lost.
	private readonly ChannelReader<bool> _signalReader = signal.Subscribe();

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try
		{
			await Task.Delay(InitialDelay, stoppingToken);
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
			return;
		}

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await ProcessPendingResolutionsAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				// Per-batch failures are logged and swallowed so the next tick can retry —
				// we never want a single bad description to crash the host. The resolver is
				// idempotent (the WHERE clause excludes already-linked rows) so retrying
				// after a transient failure re-picks the same unresolved set.
				logger.LogError(ex, "Error during normalized-description resolution cycle");
			}

			// Drain any dirty signals that arrived while we were processing. We only care
			// that at least one signal exists to wake us early; excess reads are harmless.
			while (_signalReader.TryRead(out _))
			{
			}

			try
			{
				// Wait for either the next signal or the poll interval, whichever fires first.
				// Using a linked CTS lets the stop token cancel the wait without racing with
				// the channel reader.
				using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
				waitCts.CancelAfter(Interval);
				try
				{
					await _signalReader.ReadAsync(waitCts.Token);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					break;
				}
				catch (OperationCanceledException)
				{
					// Interval elapsed — fall through to the next cycle.
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
		}
	}

	// Exposed internally so tests can invoke a single cycle directly without spinning the
	// hosted-service loop (and tolerating the 10-second initial delay). The method is
	// deliberately side-effect-bounded: matching precedes a short guarded write transaction.
	// Only unchanged snapshots are applied, with their automatic audit rows in the same commit.
	internal async Task<ResolutionSummary> ProcessPendingResolutionsAsync(CancellationToken cancellationToken)
	{
		using IServiceScope scope = scopeFactory.CreateScope();
		IEmbeddingService embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
		INormalizedDescriptionService normalizedDescriptionService =
			scope.ServiceProvider.GetRequiredService<INormalizedDescriptionService>();
		IDbContextFactory<ApplicationDbContext> contextFactory =
			scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

		// The whole NormalizedDescriptionService.GetOrCreateAsync flow hinges on the ANN
		// search — without an embedding service we'd degrade to creating an Active entry
		// per distinct description, which is cheap but misleading because the downstream
		// "auto-accept" UX presumes real similarity scoring. Skip the cycle cleanly.
		if (!embeddingService.IsConfigured)
		{
			return ResolutionSummary.Empty;
		}

		// A negative ANN result is not authoritative while canonical vectors are still being
		// rebuilt. Creating rows in that window can permanently duplicate an existing concept
		// whose obsolete vector is (correctly) excluded from search. Exact-name callers remain
		// available through GetOrCreateAsync; the background fuzzy resolver simply retries after
		// the canonical-first rebuild reaches full coverage.
		EmbeddingCoverage coverage = await normalizedDescriptionService.GetEmbeddingCoverageAsync(cancellationToken);
		if (coverage.CanonicalPending > 0)
		{
			logger.LogInformation(
				"Deferring normalized-description resolution while {PendingCount} canonical vectors rebuild",
				coverage.CanonicalPending);
			return ResolutionSummary.Empty;
		}

		using ApplicationDbContext context = contextFactory.CreateDbContext();

		// Candidate set: live ReceiptItems without an FK and with a description long enough
		// to meaningfully embed. `IgnoreQueryFilters` matches the other background services
		// (EmbeddingGenerationService): we want full visibility over the hard schema state
		// rather than relying on soft-delete filters, and the explicit DeletedAt == null
		// condition keeps the semantics identical. The ordering pin by Id keeps successive
		// cycles deterministic on an otherwise-arbitrary set and makes test assertions easier.
		// Tombstoned text is excluded here rather than only being declined further down
		// (RECEIPTS-876). Rejecting a description unlinks its items, so they become unresolved
		// again and match this predicate forever. Filtering them out at the source is what stops
		// them occupying the Take(BatchSize) window on every cycle and starving genuinely-new
		// items behind them — a queue of rejected rows would otherwise halt resolution entirely.
		//
		// The comparison mirrors the unique functional index on lower("CanonicalName"), so this is
		// an index probe per candidate rather than a scan.
		List<ReceiptItemEntity> pending = await context.ReceiptItems
			.AsNoTracking()
			.IgnoreAutoIncludes()
			.IgnoreQueryFilters()
			.Where(r =>
				r.DeletedAt == null &&
				r.NormalizedDescriptionId == null &&
				r.Description != string.Empty &&
				r.Description.Length >= MinDescriptionLength &&
				!context.NormalizedDescriptions.Any(n =>
					n.Status == NormalizedDescriptionStatus.Rejected &&
					n.CanonicalName.ToLower() == r.Description.ToLower()))
			.OrderBy(r => r.Id)
			.Take(BatchSize)
			.ToListAsync(cancellationToken);

		if (pending.Count == 0)
		{
			return ResolutionSummary.Empty;
		}

		// Read source values and revision together before any expensive matching. A later
		// edit/revert or link/unlink still changes PostgreSQL's row revision.
		List<NormalizationWriteGuard.ItemSnapshot> snapshots = await NormalizationWriteGuard.ReadItemSnapshotsAsync(
			context, pending.Select(item => item.Id).ToArray(), cancellationToken);
		List<NormalizationWriteGuard.ItemSnapshot> eligible = snapshots.Where(item => item.DeletedAt is null
			&& item.NormalizedDescriptionId is null && item.Description.Length >= MinDescriptionLength).ToList();

		// Group by raw description so we only call GetOrCreateAsync once per unique text.
		// The grouping collapses casing-equivalent duplicates (e.g., "Organic Milk" /
		// "organic milk") only if they're literally identical — the service itself handles
		// case-insensitive matching against canonical names when it sees them for the
		// first time, so the first call in a group that sees "organic MILK" will still
		// resolve to the same canonical entry as a later call with "ORGANIC milk".
		var groups = eligible
			.GroupBy(r => r.Description)
			.ToList();

		int newEntriesCreated = 0;
		int skipped = pending.Count - eligible.Count;
		List<PlannedResolution> planned = [];

		foreach (var group in groups)
		{
			cancellationToken.ThrowIfCancellationRequested();

			GetOrCreateResult? result;
			try
			{
				result = await normalizedDescriptionService.GetOrCreateAsync(group.Key, cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				// A single bad description (unexpected embedding failure, DB hiccup while the
				// normalized service tries to race-insert, etc.) must not poison the rest of
				// the batch. Count the group as skipped and continue.
				logger.LogError(
					ex,
					"Failed to resolve normalized description for {Count} receipt item(s) with text {Description}",
					group.Count(),
					group.Key);
				skipped += group.Count();
				continue;
			}

			// A tombstone the candidate filter did not catch — the text was rejected between
			// building the batch and resolving this group, or a caller reached the service by
			// another path. Leave the items unlinked; the next cycle's filter will exclude them
			// (RECEIPTS-876).
			if (result.IsRejected)
			{
				logger.LogDebug(
					"Skipping {Count} receipt item(s): description {Description} is tombstoned",
					group.Count(),
					group.Key);
				skipped += group.Count();
				continue;
			}

			// MatchScore == null means GetOrCreateAsync created a brand-new canonical entry
			// (no candidate above the pending-review floor, or the embedding service went
			// unavailable mid-call). We still link the items — the FK being present is the
			// user-visible signal that resolution happened; the null score is a truthful
			// "we didn't have a similarity to record".
			if (result.MatchScore is null)
			{
				newEntriesCreated++;
			}

			foreach (NormalizationWriteGuard.ItemSnapshot item in group)
			{
				planned.Add(new(item, result.Description.Id, result.Description.CanonicalName, result.MatchScore));
			}
		}

		int linked = await ApplyResolutionsAsync(contextFactory, planned, cancellationToken);
		skipped += planned.Count - linked;
		if (linked > 0)
		{
			await committedChangePublisher.PublishAsync(new(
				CommittedEntityType.ReceiptItem,
				CommittedChangeType.Updated,
				SuppressToast: true));
		}

		ResolutionSummary summary = new(linked, newEntriesCreated, skipped);
		logger.LogInformation(
			"Resolved normalized descriptions: linked={Linked}, newEntriesCreated={NewEntriesCreated}, skipped={Skipped}",
			summary.Linked,
			summary.NewEntriesCreated,
			summary.Skipped);

		return summary;
	}

	private sealed record PlannedResolution(NormalizationWriteGuard.ItemSnapshot Source,
		Guid TargetId, string TargetName, double? MatchScore);

	private static async Task<int> ApplyResolutionsAsync(IDbContextFactory<ApplicationDbContext> contextFactory,
		List<PlannedResolution> planned, CancellationToken cancellationToken)
	{
		if (planned.Count == 0)
		{
			return 0;
		}

		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		await using IDbContextTransaction? transaction = context.Database.IsRelational()
			? await context.Database.BeginTransactionAsync(cancellationToken) : null;
		Guid[] targetIds = planned.Select(plan => plan.TargetId).Distinct().ToArray();
		Guid[] itemIds = planned.Select(plan => plan.Source.Id).Distinct().ToArray();
		await NormalizationWriteGuard.LockTargetsAsync(context, targetIds, cancellationToken);
		await NormalizationWriteGuard.LockItemsAsync(context, itemIds, cancellationToken);
		Dictionary<Guid, NormalizedDescriptionEntity> targets = await context.NormalizedDescriptions
			.Where(target => targetIds.Contains(target.Id)).ToDictionaryAsync(target => target.Id, cancellationToken);
		// A newly rejected exact source text takes precedence over a different fuzzy target.
		// Rejection shares the write gate, so it cannot commit between this check and attachment.
		string[] sourceNames = planned.Select(plan => plan.Source.Description.Trim().ToLowerInvariant()).Distinct().ToArray();
		HashSet<string> rejectedNames = (await context.NormalizedDescriptions
			.Where(target => target.Status == NormalizedDescriptionStatus.Rejected && sourceNames.Contains(target.CanonicalName.ToLower()))
			.Select(target => target.CanonicalName.ToLower()).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
		Dictionary<Guid, ReceiptItemEntity> items = await context.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes()
			.Where(item => itemIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
		Dictionary<Guid, NormalizationWriteGuard.ItemSnapshot> current = (await NormalizationWriteGuard.ReadItemSnapshotsAsync(
			context, itemIds, cancellationToken)).ToDictionary(item => item.Id);

		int linked = 0;
		foreach (PlannedResolution plan in planned)
		{
			if (rejectedNames.Contains(plan.Source.Description.Trim().ToLowerInvariant())
				|| !current.TryGetValue(plan.Source.Id, out var snapshot) || !plan.Source.Matches(snapshot)
				|| !items.TryGetValue(plan.Source.Id, out ReceiptItemEntity? item)
				|| item.DeletedAt is not null || item.NormalizedDescriptionId is not null
				|| !targets.TryGetValue(plan.TargetId, out NormalizedDescriptionEntity? target)
				|| target.Status == NormalizedDescriptionStatus.Rejected
				|| !string.Equals(target.CanonicalName, plan.TargetName, StringComparison.Ordinal))
			{
				continue;
			}
			item.NormalizedDescriptionId = target.Id;
			item.NormalizedDescriptionMatchScore = plan.MatchScore;
			linked++;
		}

		if (linked > 0)
		{
			await context.SaveChangesAsync(cancellationToken);
		}
		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken);
		}
		return linked;
	}

	internal readonly record struct ResolutionSummary(int Linked, int NewEntriesCreated, int Skipped)
	{
		public static ResolutionSummary Empty { get; } = new(0, 0, 0);
	}
}
