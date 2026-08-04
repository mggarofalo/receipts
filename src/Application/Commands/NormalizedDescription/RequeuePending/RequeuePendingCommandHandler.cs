using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Mediator;

namespace Application.Commands.NormalizedDescription.RequeuePending;

public class RequeuePendingCommandHandler(
	INormalizedDescriptionService service,
	IDescriptionChangeSignal signal)
	: IRequestHandler<RequeuePendingCommand, RequeuePendingResult?>
{
	public async ValueTask<RequeuePendingResult?> Handle(RequeuePendingCommand request, CancellationToken cancellationToken)
	{
		RequeuePendingResult? result = await service.RequeuePendingAsync(request.ExpectedFingerprint, cancellationToken);

		// Wake the resolver immediately instead of leaving the requeued items idle for up to a full
		// poll interval. Only on a run that actually unlinked something: a rejected guard (null) or
		// a no-op re-run has nothing for the resolver to pick up.
		if (result is { UnlinkedItemCount: > 0 })
		{
			signal.NotifyDirty();
		}

		return result;
	}
}
