using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Queries.Core.Ynab;

public class GetYnabAccountMappingByIdQueryHandler(IYnabAccountMappingService accountMappingService) : IRequestHandler<GetYnabAccountMappingByIdQuery, YnabAccountMappingDto?>
{
	public async Task<YnabAccountMappingDto?> Handle(GetYnabAccountMappingByIdQuery request, CancellationToken cancellationToken)
	{
		return await accountMappingService.GetByIdAsync(request.Id, cancellationToken);
	}
}
