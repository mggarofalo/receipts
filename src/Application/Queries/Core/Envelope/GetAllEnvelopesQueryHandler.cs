using Application.Interfaces.Services;
using Mediator;

namespace Application.Queries.Core.Envelope;

public class GetAllEnvelopesQueryHandler(IEnvelopeService service) : IRequestHandler<GetAllEnvelopesQuery, List<Domain.Envelopes.Envelope>>
{
	public async ValueTask<List<Domain.Envelopes.Envelope>> Handle(GetAllEnvelopesQuery request, CancellationToken cancellationToken)
		=> await service.GetAllAsync(request.IsActive, cancellationToken);
}
