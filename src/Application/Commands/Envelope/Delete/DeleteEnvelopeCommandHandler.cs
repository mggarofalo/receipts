using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Envelope.Delete;

public class DeleteEnvelopeCommandHandler(IEnvelopeService service) : IRequestHandler<DeleteEnvelopeCommand, bool>
{
	public async ValueTask<bool> Handle(DeleteEnvelopeCommand request, CancellationToken cancellationToken)
		=> await service.DeleteAsync(request.Id, cancellationToken);
}
