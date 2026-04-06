using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Queries.Core.Ynab;

public class GetYnabBudgetsQueryHandler(IYnabApiClient ynabApiClient) : IRequestHandler<GetYnabBudgetsQuery, List<YnabBudget>>
{
	public async Task<List<YnabBudget>> Handle(GetYnabBudgetsQuery request, CancellationToken cancellationToken)
	{
		return await ynabApiClient.GetBudgetsAsync(cancellationToken);
	}
}
