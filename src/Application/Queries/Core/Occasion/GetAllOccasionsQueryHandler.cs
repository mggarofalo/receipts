using Application.Interfaces.Services;
using Mediator;

namespace Application.Queries.Core.Occasion;

public class GetAllOccasionsQueryHandler(IOccasionService service) : IRequestHandler<GetAllOccasionsQuery, List<Domain.Envelopes.Occasion>>
{
	public async ValueTask<List<Domain.Envelopes.Occasion>> Handle(GetAllOccasionsQuery request, CancellationToken cancellationToken)
		=> await service.GetAllAsync(request.IsActive, cancellationToken);
}
