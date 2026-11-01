using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Mediator;

namespace Application.Commands.NormalizedDescription.UndoPendingNamePromotion;

public class UndoPendingNamePromotionCommandHandler(IPendingNamePromotionService service)
	: IRequestHandler<UndoPendingNamePromotionCommand, PendingNamePromotionUndo>
{
	public async ValueTask<PendingNamePromotionUndo> Handle(UndoPendingNamePromotionCommand request, CancellationToken cancellationToken)
		=> await service.UndoAsync(request.Actor, request.PromotionId, cancellationToken);
}
