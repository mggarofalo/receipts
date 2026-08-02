using System.Threading.Channels;
using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
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
	ILogger<NormalizedDescriptionResolutionService> logger) : BackgroundService
{
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
	// deliberately side-effect-bounded: either all grouped updates persist and the cycle
	// returns a summary, or a failure bubbles out and no FKs are written (the SaveChanges
	// call is single-shot across all items in the batch).
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

		using ApplicationDbContext context = contextFactory.CreateDbContext();

		// Candidate set: live ReceiptItems without an FK and with a description long enough
		// to meaningfully embed. `IgnoreQueryFilters` matches the other background services
		// (EmbeddingGenerationService): we want full visibility over the hard schema state
		// rather than relying on soft-delete filters, and the explicit DeletedAt == null
		// condition keeps the semantics identical. The ordering pin by Id keeps successive
		// cycles deterministic on an otherwise-arbitrary set and makes test assertions easier.
		List<ReceiptItemEntity> pending = await context.ReceiptItems
			.IgnoreQueryFilters()
			.Where(r =>
				r.DeletedAt == null &&
				r.NormalizedDescriptionId == null &&
				r.Description != string.Empty &&
				r.Description.Length >= MinDescriptionLength)
			.OrderBy(r => r.Id)
			.Take(BatchSize)
			.ToListAsync(cancellationToken);

		if (pending.Count == 0)
		{
			return ResolutionSummary.Empty;
		}

		// Group by raw description so we only call GetOrCreateAsync once per unique text.
		// The grouping collapses casing-equivalent duplicates (e.g., "Organic Milk" /
		// "organic milk") only if they're literally identical — the service itself handles
		// case-insensitive matching against canonical names when it sees them for the
		// first time, so the first call in a group that sees "organic MILK" will still
		// resolve to the same canonical entry as a later call with "ORGANIC milk".
		var groups = pending
			.GroupBy(r => r.Description)
			.ToList();

		int linked = 0;
		int newEntriesCreated = 0;
		int skipped = 0;

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

			// MatchScore == null means GetOrCreateAsync created a brand-new canonical entry
			// (no candidate above the pending-review floor, or the embedding service went
			// unavailable mid-call). We still link the items — the FK being present is the
			// user-visible signal that resolution happened; the null score is a truthful
			// "we didn't have a similarity to record".
			if (result.MatchScore is null)
			{
				newEntriesCreated++;
			}

			foreach (ReceiptItemEntity item in group)
			{
				item.NormalizedDescriptionId = result.Description.Id;
				item.NormalizedDescriptionMatchScore = result.MatchScore;
				linked++;
			}
		}

		if (linked > 0)
		{
			await context.SaveChangesAsync(cancellationToken);
		}

		ResolutionSummary summary = new(linked, newEntriesCreated, skipped);
		logger.LogInformation(
			"Resolved normalized descriptions: linked={Linked}, newEntriesCreated={NewEntriesCreated}, skipped={Skipped}",
			summary.Linked,
			summary.NewEntriesCreated,
			summary.Skipped);

		return summary;
	}

	internal readonly record struct ResolutionSummary(int Linked, int NewEntriesCreated, int Skipped)
	{
		public static ResolutionSummary Empty { get; } = new(0, 0, 0);
	}
}
