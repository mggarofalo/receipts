using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Queries.Core.Ynab;

public class GetYnabCategoriesQueryHandler(IYnabApiClient ynabApiClient) : IRequestHandler<GetYnabCategoriesQuery, List<YnabCategory>>
{
	public async Task<List<YnabCategory>> Handle(GetYnabCategoriesQuery request, CancellationToken cancellationToken)
	{
		return await ynabApiClient.GetCategoriesAsync(request.BudgetId, cancellationToken);
	}
}
