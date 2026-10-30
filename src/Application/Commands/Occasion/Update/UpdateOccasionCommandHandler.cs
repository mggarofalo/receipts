using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Occasion.Update;

public class UpdateOccasionCommandHandler(IOccasionService service) : IRequestHandler<UpdateOccasionCommand, bool>
{
	public async ValueTask<bool> Handle(UpdateOccasionCommand request, CancellationToken cancellationToken)
		=> await service.UpdateAsync(request.Occasion, cancellationToken);
}
