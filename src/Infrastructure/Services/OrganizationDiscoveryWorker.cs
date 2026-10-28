using Application.Classification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>Resumes durable discovery after restart. Never sends provider requests or applies classifications.</summary>
public sealed class OrganizationDiscoveryWorker(IServiceScopeFactory scopes,
	ILogger<OrganizationDiscoveryWorker> logger) : BackgroundService
{
	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan PassTimeout = TimeSpan.FromSeconds(15);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				using var pass = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
				pass.CancelAfter(PassTimeout);
				await RunOnceAsync(pass.Token);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
			catch (Exception exception)
			{
				logger.LogWarning("Organization discovery deferred ({ExceptionType})", exception.GetType().Name);
			}
			try { await Task.Delay(PollInterval, stoppingToken); }
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
		}
	}

	/// <summary>One bounded page per inbox/sweep; provider absence does not block local discovery.</summary>
	public async Task<int> RunOnceAsync(CancellationToken ct = default)
	{
		await using AsyncServiceScope scope = scopes.CreateAsyncScope();
		var settings = await scope.ServiceProvider.GetRequiredService<ICategoryAssistanceSettingsStore>().GetAsync(ct);
		if (!settings.Enabled || !CategoryAssistancePolicy.IsValid(settings)) { return 0; }
		var store = scope.ServiceProvider.GetRequiredService<OrganizationDiscoveryStore>();
		Guid run = await store.StartAsync(settings.Revision, ct);
		return run == Guid.Empty ? 0 : await store.DiscoverAsync(run, OrganizationDiscoveryStore.MaximumChunkSize, ct);
	}
}
