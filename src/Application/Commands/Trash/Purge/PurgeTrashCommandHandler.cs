using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Trash.Purge;

public class PurgeTrashCommandHandler(ITrashService trashService) : IRequestHandler<PurgeTrashCommand, PurgeTrashResult>
{
	public async ValueTask<PurgeTrashResult> Handle(PurgeTrashCommand request, CancellationToken cancellationToken)
	{
		return await trashService.PurgeAllDeletedAsync(cancellationToken);
	}
}
