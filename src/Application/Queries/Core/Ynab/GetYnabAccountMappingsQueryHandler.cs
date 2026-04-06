using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Queries.Core.Ynab;

public class GetYnabAccountMappingsQueryHandler(IYnabAccountMappingService accountMappingService) : IRequestHandler<GetYnabAccountMappingsQuery, List<YnabAccountMappingDto>>
{
	public async Task<List<YnabAccountMappingDto>> Handle(GetYnabAccountMappingsQuery request, CancellationToken cancellationToken)
	{
		return await accountMappingService.GetAllAsync(cancellationToken);
	}
}
