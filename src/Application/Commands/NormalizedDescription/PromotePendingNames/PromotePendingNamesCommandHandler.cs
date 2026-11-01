using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Mediator;

namespace Application.Commands.NormalizedDescription.PromotePendingNames;

public class PromotePendingNamesCommandHandler(IPendingNamePromotionService service)
	: IRequestHandler<PromotePendingNamesCommand, PendingNamePromotionResult?>
{
	public async ValueTask<PendingNamePromotionResult?> Handle(PromotePendingNamesCommand request, CancellationToken cancellationToken)
		=> await service.PromoteAsync(request.Actor, request.ExpectedFingerprint, cancellationToken);
}
