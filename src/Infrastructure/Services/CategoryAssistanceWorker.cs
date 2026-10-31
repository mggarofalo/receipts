using Application.Classification;
using Infrastructure.Jev;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>Sequential, bounded polling of post-consent save observations, then of strong matches to write.</summary>
public sealed class CategoryAssistanceWorker(
		IServiceScopeFactory scopes,
		IOptionsMonitor<JevOptions> options,
		ILogger<CategoryAssistanceWorker> logger) : BackgroundService
{
	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await RunOnceAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
			catch (Exception exception)
			{
				logger.LogWarning("Category assistance polling deferred ({ExceptionType})",
						exception.GetType().Name);
			}

			try { await Task.Delay(PollInterval, stoppingToken); }
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
		}
	}

	/// <summary>One bounded pass. No history scan or provider call follows absent consent/configuration.</summary>
	public async Task<bool> RunOnceAsync(CancellationToken ct = default)
	{
		await using AsyncServiceScope scope = scopes.CreateAsyncScope();
		var settings = await scope.ServiceProvider.GetRequiredService<ICategoryAssistanceSettingsStore>()
				.GetAsync(ct);
		JevOptions current = options.CurrentValue;
		if (!settings.Enabled || !current.Enabled || string.IsNullOrWhiteSpace(current.ApiKey)
				|| settings.Provider != "jev" || settings.Model != CategorySuggestionPolicy.Model)
		{
			return false;
		}

		await scope.ServiceProvider.GetRequiredService<ICategoryAssistanceConnectionReconciler>()
				.ReconcileAsync(JevCategoryAssistanceSelectedProvider.ConfigurationFor(current.ApiKey), ct);
		bool dispatched = await scope.ServiceProvider.GetRequiredService<CategoryAssistanceDispatcher>()
				.DispatchOneAsync(ct);
		bool applied = await scope.ServiceProvider.GetRequiredService<CategoryAssistanceAutoWriter>()
				.ApplyNextAsync(ct);
		return dispatched || applied;
	}
}
