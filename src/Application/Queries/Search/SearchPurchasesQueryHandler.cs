using Application.Interfaces.Services;
using Application.Models.Search;
using Mediator;

namespace Application.Queries.Search;

public class SearchPurchasesQueryHandler(IPurchaseSearchReader reader)
	: IRequestHandler<SearchPurchasesQuery, PurchaseSearchResult>
{
	public async ValueTask<PurchaseSearchResult> Handle(SearchPurchasesQuery request, CancellationToken cancellationToken)
		=> await reader.SearchAsync(request.Q.Trim(), request.Offset, request.Limit, cancellationToken);
}
