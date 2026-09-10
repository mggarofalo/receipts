using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class EmbeddingModelWarmupService(
	IEmbeddingModelRuntime runtime,
	Application.Interfaces.Services.IEmbeddingService embeddingService,
	ILogger<EmbeddingModelWarmupService> logger) : BackgroundService
{
	private static readonly TimeSpan AvailabilityPollInterval = TimeSpan.FromSeconds(5);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try
		{
			while (!stoppingToken.IsCancellationRequested && !embeddingService.IsConfigured)
			{
				await Task.Delay(AvailabilityPollInterval, stoppingToken);
			}
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
			return;
		}

		if (stoppingToken.IsCancellationRequested || runtime.IsLoaded)
		{
			return;
		}

		using Process process = Process.GetCurrentProcess();
		process.Refresh();
		long memoryBefore = process.WorkingSet64;
		long started = Stopwatch.GetTimestamp();
		try
		{
			await runtime.WarmUpAsync(stoppingToken);
			process.Refresh();
			long memoryDelta = process.WorkingSet64 - memoryBefore;
			logger.LogInformation(
				"Warmed embedding model in {ElapsedMilliseconds:F0} ms; process working set changed by {MemoryDeltaBytes} bytes",
				Stopwatch.GetElapsedTime(started).TotalMilliseconds,
				memoryDelta);
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Embedding model warmup failed; semantic requests will retry through the bounded queue");
		}
	}
}
