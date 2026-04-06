using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Queries.Core.Ynab;

public class GetAllYnabCategoryMappingsQueryHandler(IYnabCategoryMappingService service) : IRequestHandler<GetAllYnabCategoryMappingsQuery, List<YnabCategoryMappingDto>>
{
	public async Task<List<YnabCategoryMappingDto>> Handle(GetAllYnabCategoryMappingsQuery request, CancellationToken cancellationToken)
	{
		return await service.GetAllAsync(cancellationToken);
	}
}
