using Application.Interfaces.Services;
using Application.Models;
using Mediator;

namespace Application.Queries.Core.Receipt;

public class GetAllReceiptsQueryHandler(IReceiptService receiptService) : IRequestHandler<GetAllReceiptsQuery, PagedResult<Domain.Core.Receipt>>
{
	public async ValueTask<PagedResult<Domain.Core.Receipt>> Handle(GetAllReceiptsQuery request, CancellationToken cancellationToken)
	{
		return await receiptService.GetAllAsync(request.Offset, request.Limit, request.Sort, request.AccountId, request.CardId, request.Q, request.Location, cancellationToken);
	}
}
