using Application.Interfaces.Services;
using Application.Models;
using Mediator;

namespace Application.Queries.Core.Subcategory;

public class GetSubcategoriesByCategoryIdQueryHandler(ISubcategoryService subcategoryService) : IRequestHandler<GetSubcategoriesByCategoryIdQuery, PagedResult<Domain.Core.Subcategory>>
{
	public async ValueTask<PagedResult<Domain.Core.Subcategory>> Handle(GetSubcategoriesByCategoryIdQuery request, CancellationToken cancellationToken)
	{
		return await subcategoryService.GetByCategoryIdAsync(request.CategoryId, request.Offset, request.Limit, request.Sort, request.IsActive, request.Q, cancellationToken);
	}
}
