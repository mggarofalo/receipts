using Application.Interfaces.Services;
using Mediator;

namespace Application.Queries.Core.Envelope;

public class GetEnvelopeByIdQueryHandler(IEnvelopeService service) : IRequestHandler<GetEnvelopeByIdQuery, Domain.Envelopes.Envelope?>
{
	public async ValueTask<Domain.Envelopes.Envelope?> Handle(GetEnvelopeByIdQuery request, CancellationToken cancellationToken)
		=> await service.GetByIdAsync(request.Id, cancellationToken);
}
