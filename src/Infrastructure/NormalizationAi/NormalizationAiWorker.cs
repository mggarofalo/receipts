using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.NormalizationAi;

public sealed class NormalizationAiWorker(IServiceScopeFactory scopes, ILogger<NormalizationAiWorker> logger, IOptions<NormalizationAiOptions> options) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				using IServiceScope scope = scopes.CreateScope();
				NormalizationAiService service = scope.ServiceProvider.GetRequiredService<NormalizationAiService>();
				if (!options.Value.Enabled || string.IsNullOrWhiteSpace(options.Value.OwnerUserId))
				{
					// The kill switch stops inference, not privacy retention or lease recovery.
					await service.RecoverExpiredAsync(stoppingToken);
					await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
					continue;
				}
				bool processed = await service.ProcessNextAsync(stoppingToken);
				if (!processed) { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
			catch (Exception exception)
			{
				logger.LogWarning("Normalization worker could not finish its cycle ({ExceptionType}); durable state will be recovered", exception.GetType().Name);
				try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
			}
		}
	}
}
