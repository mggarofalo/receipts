using Application.NormalizationAi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.NormalizationAi;

/// <summary>Register once per host alongside the sign-in singleton. Immediate startup reconciliation
/// and periodic retries cover failed finalization without replaying the provider ceremony.</summary>
public sealed class ChatGptConnectionRecoveryWorker(IServiceScopeFactory scopes, TimeProvider clock,
	ILogger<ChatGptConnectionRecoveryWorker> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using PeriodicTimer timer = new(TimeSpan.FromSeconds(10), clock);
		do
		{
			try
			{
				await using AsyncServiceScope scope = scopes.CreateAsyncScope();
				await scope.ServiceProvider.GetRequiredService<INormalizationAiService>().RecoverConnectionChangeAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
			catch (Exception) { logger.LogWarning("ChatGPT connection recovery remains unavailable; guarded reconciliation will retry."); }
		} while (await timer.WaitForNextTickAsync(stoppingToken));
	}
}
