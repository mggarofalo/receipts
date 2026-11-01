using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Mediator;

namespace Application.Queries.NormalizedDescription.PreviewPendingNamePromotion;

public class PreviewPendingNamePromotionQueryHandler(IPendingNamePromotionService service)
	: IRequestHandler<PreviewPendingNamePromotionQuery, PendingNamePromotionPreview>
{
	public async ValueTask<PendingNamePromotionPreview> Handle(PreviewPendingNamePromotionQuery request, CancellationToken cancellationToken)
		=> await service.PreviewAsync(cancellationToken);
}
