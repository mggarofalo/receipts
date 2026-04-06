namespace Application.Models.Ynab;

public record YnabRateLimitStatus(
	int RemainingRequests,
	int MaxRequests,
	int RequestsUsed,
	DateTimeOffset WindowResetAt,
	DateTimeOffset? OldestRequestAt);
