using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class EmbeddingModelWarmupService(
	IEmbeddingModelRuntime runtime,
	ILogger<EmbeddingModelWarmupService> logger) : BackgroundService
{
	private static readonly TimeSpan AvailabilityPollInterval = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan FailedWarmupRetryInterval = TimeSpan.FromSeconds(30);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				while (!runtime.IsProvisioned)
				{
					await Task.Delay(AvailabilityPollInterval, stoppingToken);
				}

				if (runtime.IsReady)
				{
					return;
				}

				using Process process = Process.GetCurrentProcess();
				process.Refresh();
				long memoryBefore = process.WorkingSet64;
				long started = Stopwatch.GetTimestamp();
				await runtime.WarmUpAsync(stoppingToken);
				process.Refresh();
				long memoryDelta = process.WorkingSet64 - memoryBefore;
				logger.LogInformation(
					"Warmed embedding model in {ElapsedMilliseconds:F0} ms; process working set changed by {MemoryDeltaBytes} bytes",
					Stopwatch.GetElapsedTime(started).TotalMilliseconds,
					memoryDelta);
				return;
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Embedding model warmup failed; retrying after {RetryDelay}", FailedWarmupRetryInterval);
				await Task.Delay(FailedWarmupRetryInterval, stoppingToken);
			}
		}
	}
}
