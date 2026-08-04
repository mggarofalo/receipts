using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Mediator;

namespace Application.Queries.NormalizedDescription.GetAll;

public class GetAllNormalizedDescriptionsQueryHandler(INormalizedDescriptionService service)
	: IRequestHandler<GetAllNormalizedDescriptionsQuery, List<NormalizedDescriptionDetail>>
{
	public async ValueTask<List<NormalizedDescriptionDetail>> Handle(GetAllNormalizedDescriptionsQuery request, CancellationToken cancellationToken)
	{
		return await service.GetAllAsync(request.StatusFilter, cancellationToken);
	}
}
