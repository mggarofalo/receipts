namespace Application.Models.Ynab;

// Aggregated health snapshot for the /ynab status page. Combines signals
// from YnabSyncEvent (counts) with the already-existing connection and
// rate-limit surfaces so the frontend can render the health grid in one call.
public record YnabStatusResult(
	bool IsConfigured,
	bool IsConnected,
	string? SelectedBudgetId,
	DateTimeOffset? LastSuccessUtc,
	DateTimeOffset? LastFailureUtc,
	int Pushes24h,
	int Successes24h,
	int Failures24h,
	int Pushes7d,
	int Successes7d,
	int Failures7d,
	int Pushes30d,
	int Successes30d,
	int Failures30d,
	YnabRateLimitStatus RateLimit);
