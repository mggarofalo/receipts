using Application.Interfaces.Services;
using MediatR;

namespace Application.Queries.Core.Ynab;

public class GetUnmappedCategoriesQueryHandler(IYnabCategoryMappingService service) : IRequestHandler<GetUnmappedCategoriesQuery, List<string>>
{
	public async Task<List<string>> Handle(GetUnmappedCategoriesQuery request, CancellationToken cancellationToken)
	{
		return await service.GetUnmappedCategoriesAsync(cancellationToken);
	}
}
