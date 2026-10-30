using Application.Interfaces.Services;
using Mediator;

namespace Application.Queries.Core.ItemTypeEnvelopeDefault;

public class GetItemTypeEnvelopeDefaultsQueryHandler(IItemTypeEnvelopeDefaultService service)
	: IRequestHandler<GetItemTypeEnvelopeDefaultsQuery, List<Interfaces.Services.ItemTypeEnvelopeDefault>>
{
	public async ValueTask<List<Interfaces.Services.ItemTypeEnvelopeDefault>> Handle(GetItemTypeEnvelopeDefaultsQuery request, CancellationToken cancellationToken)
		=> await service.GetAllAsync(cancellationToken);
}
