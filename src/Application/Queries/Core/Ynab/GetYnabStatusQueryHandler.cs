using Application.Interfaces.Services;
using Application.Models.Ynab;
using Common;
using Mediator;

namespace Application.Queries.Core.Ynab;

public class GetYnabStatusQueryHandler(
	IYnabApiClient ynabApiClient,
	IYnabBudgetSelectionService budgetSelectionService,
	IYnabSyncEventService syncEventService,
	IYnabRateLimitTracker rateLimitTracker,
	TimeProvider timeProvider) : IRequestHandler<GetYnabStatusQuery, YnabStatusResult>
{
	public async ValueTask<YnabStatusResult> Handle(GetYnabStatusQuery request, CancellationToken cancellationToken)
	{
		bool isConfigured = ynabApiClient.IsConfigured;

		// When YNAB isn't configured we still return a result so the page can render
		// a "not connected" state; everything zero / null.
		if (!isConfigured)
		{
			return new YnabStatusResult(
				IsConfigured: false,
				IsConnected: false,
				SelectedBudgetId: null,
				LastSuccessUtc: null,
				LastFailureUtc: null,
				Pushes24h: 0, Successes24h: 0, Failures24h: 0,
				Pushes7d: 0, Successes7d: 0, Failures7d: 0,
				Pushes30d: 0, Successes30d: 0, Failures30d: 0,
				RateLimit: rateLimitTracker.GetStatus());
		}

		bool isConnected;
		try
		{
			await ynabApiClient.GetBudgetsAsync(cancellationToken);
			isConnected = true;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			isConnected = false;
		}

		string? selectedBudgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);

		DateTimeOffset? lastSuccess = await syncEventService.GetLatestOccurrenceAsync(YnabSyncStatus.Synced, cancellationToken);
		DateTimeOffset? lastFailure = await syncEventService.GetLatestOccurrenceAsync(YnabSyncStatus.Failed, cancellationToken);

		DateTimeOffset now = timeProvider.GetUtcNow();
		DateTimeOffset since24h = now.AddHours(-24);
		DateTimeOffset since7d = now.AddDays(-7);
		DateTimeOffset since30d = now.AddDays(-30);

		// Three rolling windows × three buckets each (total / success / failure).
		// The total bucket is computed by passing outcome=null.
		int p24 = await syncEventService.CountSinceAsync(since24h, null, cancellationToken);
		int s24 = await syncEventService.CountSinceAsync(since24h, YnabSyncStatus.Synced, cancellationToken);
		int f24 = await syncEventService.CountSinceAsync(since24h, YnabSyncStatus.Failed, cancellationToken);

		int p7 = await syncEventService.CountSinceAsync(since7d, null, cancellationToken);
		int s7 = await syncEventService.CountSinceAsync(since7d, YnabSyncStatus.Synced, cancellationToken);
		int f7 = await syncEventService.CountSinceAsync(since7d, YnabSyncStatus.Failed, cancellationToken);

		int p30 = await syncEventService.CountSinceAsync(since30d, null, cancellationToken);
		int s30 = await syncEventService.CountSinceAsync(since30d, YnabSyncStatus.Synced, cancellationToken);
		int f30 = await syncEventService.CountSinceAsync(since30d, YnabSyncStatus.Failed, cancellationToken);

		return new YnabStatusResult(
			IsConfigured: true,
			IsConnected: isConnected,
			SelectedBudgetId: selectedBudgetId,
			LastSuccessUtc: lastSuccess,
			LastFailureUtc: lastFailure,
			Pushes24h: p24, Successes24h: s24, Failures24h: f24,
			Pushes7d: p7, Successes7d: s7, Failures7d: f7,
			Pushes30d: p30, Successes30d: s30, Failures30d: f30,
			RateLimit: rateLimitTracker.GetStatus());
	}
}
