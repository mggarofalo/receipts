using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

public sealed class NormalizationReviewInboxWorker(IServiceScopeFactory scopes, ILogger<NormalizationReviewInboxWorker> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				using var scope = scopes.CreateScope();
				var service = scope.ServiceProvider.GetRequiredService<NormalizationReviewInboxService>();
				await service.ReconcileAsync(stoppingToken);
				await service.ScheduleNextAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
			catch (Exception exception) { logger.LogWarning("Review inbox reconciliation deferred ({ExceptionType})", exception.GetType().Name); }
			try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
		}
	}
}
