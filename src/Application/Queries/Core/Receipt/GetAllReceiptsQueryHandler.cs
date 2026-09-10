using Application.Interfaces.Services;
using Application.Models;
using Mediator;

namespace Application.Queries.Core.Receipt;

public class GetAllReceiptsQueryHandler(IReceiptListReader reader) : IRequestHandler<GetAllReceiptsQuery, PagedResult<ReceiptListItem>>
{
	public async ValueTask<PagedResult<ReceiptListItem>> Handle(GetAllReceiptsQuery request, CancellationToken cancellationToken)
	{
		return await reader.GetAsync(request.Offset, request.Limit, request.Sort, request.AccountId, request.CardId, request.Q, request.Location, cancellationToken);
	}
}
