using Application.Interfaces.Services;
using Application.Models;
using Mediator;

namespace Application.Queries.Core.Subcategory;

public class GetAllSubcategoriesQueryHandler(ISubcategoryService subcategoryService) : IRequestHandler<GetAllSubcategoriesQuery, PagedResult<Domain.Core.Subcategory>>
{
	public async ValueTask<PagedResult<Domain.Core.Subcategory>> Handle(GetAllSubcategoriesQuery request, CancellationToken cancellationToken)
	{
		return await subcategoryService.GetAllAsync(request.Offset, request.Limit, request.Sort, request.IsActive, request.Q, cancellationToken);
	}
}
