using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Mediator;

namespace Application.Queries.NormalizedDescription.PreviewRequeuePending;

public class PreviewRequeuePendingQueryHandler(INormalizedDescriptionService service)
	: IRequestHandler<PreviewRequeuePendingQuery, RequeuePendingPreview>
{
	public async ValueTask<RequeuePendingPreview> Handle(PreviewRequeuePendingQuery request, CancellationToken cancellationToken)
	{
		return await service.PreviewRequeuePendingAsync(cancellationToken);
	}
}
