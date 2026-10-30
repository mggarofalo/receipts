using Application.Interfaces.Services;
using Mediator;

namespace Application.Queries.Core.Occasion;

public class GetOccasionByIdQueryHandler(IOccasionService service) : IRequestHandler<GetOccasionByIdQuery, Domain.Envelopes.Occasion?>
{
	public async ValueTask<Domain.Envelopes.Occasion?> Handle(GetOccasionByIdQuery request, CancellationToken cancellationToken)
		=> await service.GetByIdAsync(request.Id, cancellationToken);
}
