using Application.Models.Ynab;

namespace Application.Interfaces.Services;

public interface IYnabRateLimitTracker
{
	void RecordRequest();

	/// <summary>
	/// Record YNAB's authoritative rate-limit counts parsed from the <c>X-Rate-Limit</c>
	/// response header. When a recent server snapshot exists it takes precedence over the
	/// in-process estimate in <see cref="GetStatus"/>.
	/// </summary>
	void RecordServerRateLimit(int used, int limit);

	YnabRateLimitStatus GetStatus();
	bool CanMakeRequests(int count);
}
