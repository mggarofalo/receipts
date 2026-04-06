using Application.Models.Ynab;

namespace Application.Interfaces.Services;

public interface IYnabRateLimitTracker
{
	void RecordRequest();
	YnabRateLimitStatus GetStatus();
	bool CanMakeRequests(int count);
}
