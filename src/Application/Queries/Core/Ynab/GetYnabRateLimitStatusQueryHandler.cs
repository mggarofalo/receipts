using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Queries.Core.Ynab;

public class GetYnabRateLimitStatusQueryHandler(IYnabRateLimitTracker rateLimitTracker) : IRequestHandler<GetYnabRateLimitStatusQuery, YnabRateLimitStatus>
{
	public Task<YnabRateLimitStatus> Handle(GetYnabRateLimitStatusQuery request, CancellationToken cancellationToken)
	{
		return Task.FromResult(rateLimitTracker.GetStatus());
	}
}
