using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Envelope.Create;

public class CreateEnvelopeCommandHandler(IEnvelopeService service) : IRequestHandler<CreateEnvelopeCommand, Domain.Envelopes.Envelope>
{
	public async ValueTask<Domain.Envelopes.Envelope> Handle(CreateEnvelopeCommand request, CancellationToken cancellationToken)
		=> await service.CreateAsync(request.Envelope, cancellationToken);
}
