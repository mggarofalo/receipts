using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Mediator;

namespace Application.Queries.NormalizedDescription.GetById;

public class GetNormalizedDescriptionByIdQueryHandler(INormalizedDescriptionService service)
	: IRequestHandler<GetNormalizedDescriptionByIdQuery, NormalizedDescriptionDetail?>
{
	public async ValueTask<NormalizedDescriptionDetail?> Handle(GetNormalizedDescriptionByIdQuery request, CancellationToken cancellationToken)
	{
		return await service.GetByIdAsync(request.Id, cancellationToken);
	}
}
