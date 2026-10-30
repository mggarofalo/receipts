using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Envelope.Update;

public class UpdateEnvelopeCommandHandler(IEnvelopeService service) : IRequestHandler<UpdateEnvelopeCommand, bool>
{
	public async ValueTask<bool> Handle(UpdateEnvelopeCommand request, CancellationToken cancellationToken)
		=> await service.UpdateAsync(request.Envelope, cancellationToken);
}
