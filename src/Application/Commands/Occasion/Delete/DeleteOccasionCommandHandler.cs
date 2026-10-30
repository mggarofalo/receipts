using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Occasion.Delete;

public class DeleteOccasionCommandHandler(IOccasionService service) : IRequestHandler<DeleteOccasionCommand, bool>
{
	public async ValueTask<bool> Handle(DeleteOccasionCommand request, CancellationToken cancellationToken)
		=> await service.DeleteAsync(request.Id, cancellationToken);
}
