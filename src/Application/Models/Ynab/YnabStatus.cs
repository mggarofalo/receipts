namespace Application.Models.Ynab;

/// <summary>
/// Aggregate health snapshot for the /ynab status page. Derived from the append-only
/// YnabSyncEvent log plus the PAT-configured flag — deliberately cheap (no live YNAB call)
/// so the page can poll it. Connection liveness is validated separately via
/// <c>/api/ynab/connection-status</c>.
/// </summary>
public record YnabStatus(
	bool IsConfigured,
	DateTimeOffset? LastValidatedAt,
	DateTimeOffset? LastPushSuccessAt,
	DateTimeOffset? LastPushFailureAt,
	int PushCountLast24h,
	int PushCountLast7d,
	int PushCountLast30d,
	int PushSuccessLast30d,
	int PushFailureLast30d);
