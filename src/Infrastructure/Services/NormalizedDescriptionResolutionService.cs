using System.Threading.Channels;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

// Reconciles approved scoped mappings only. Similarity and canonical spelling are advisory,
// never background permission to create identities or verify receipt purchases.
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
		IDbContextFactory<ApplicationDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
		var result = await NormalizationCatalogResolver.ResolveAsync(factory, cancellationToken);
		if (result.Linked > 0)
		{
			await committedChangePublisher.PublishAsync(new(CommittedEntityType.ReceiptItem, CommittedChangeType.Updated, SuppressToast: true));
		}
		return new(result.Linked, result.Attempted - result.Linked);
	}

	internal readonly record struct ResolutionSummary(int Linked, int Skipped)
	{
		public static ResolutionSummary Empty { get; } = new(0, 0);
	}
}
