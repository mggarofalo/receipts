using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Occasion.Create;

public class CreateOccasionCommandHandler(IOccasionService service) : IRequestHandler<CreateOccasionCommand, Domain.Envelopes.Occasion>
{
	public async ValueTask<Domain.Envelopes.Occasion> Handle(CreateOccasionCommand request, CancellationToken cancellationToken)
		=> await service.CreateAsync(request.Occasion, cancellationToken);
}
