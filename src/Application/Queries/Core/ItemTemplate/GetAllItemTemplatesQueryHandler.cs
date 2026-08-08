using Application.Interfaces.Services;
using Application.Models;
using Mediator;

namespace Application.Queries.Core.ItemTemplate;

public class GetAllItemTemplatesQueryHandler(IItemTemplateService itemTemplateService) : IRequestHandler<GetAllItemTemplatesQuery, PagedResult<Domain.Core.ItemTemplate>>
{
	public async ValueTask<PagedResult<Domain.Core.ItemTemplate>> Handle(GetAllItemTemplatesQuery request, CancellationToken cancellationToken)
	{
		// Always the search path: with a null term it is the unfiltered list, so there is no second
		// code path to keep in step (RECEIPTS-930).
		return await itemTemplateService.SearchAsync(request.Q, request.Offset, request.Limit, request.Sort, cancellationToken);
	}
}
