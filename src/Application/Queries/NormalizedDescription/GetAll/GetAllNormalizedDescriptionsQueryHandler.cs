using Application.Interfaces.Services;
using Application.Models;
using Application.Models.NormalizedDescriptions;
using Mediator;

namespace Application.Queries.NormalizedDescription.GetAll;

public class GetAllNormalizedDescriptionsQueryHandler(INormalizedDescriptionService service)
	: IRequestHandler<GetAllNormalizedDescriptionsQuery, PagedResult<NormalizedDescriptionDetail>>
{
	public async ValueTask<PagedResult<NormalizedDescriptionDetail>> Handle(GetAllNormalizedDescriptionsQuery request, CancellationToken cancellationToken)
	{
		return await service.GetAllAsync(
			request.StatusFilter,
			request.Q,
			request.Offset,
			request.Limit,
			cancellationToken);
	}
}
