using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Mediator;

namespace Application.Queries.NormalizedDescription.ListPendingNamePromotions;

public class ListPendingNamePromotionsQueryHandler(IPendingNamePromotionService service)
	: IRequestHandler<ListPendingNamePromotionsQuery, IReadOnlyList<PendingNamePromotion>>
{
	public async ValueTask<IReadOnlyList<PendingNamePromotion>> Handle(ListPendingNamePromotionsQuery request, CancellationToken cancellationToken)
		=> await service.ListAsync(cancellationToken);
}
