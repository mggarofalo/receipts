using Application.Interfaces.Services;
using Application.Models;
using Mediator;

namespace Application.Queries.Core.ReceiptItem;

public class GetAllReceiptItemsQueryHandler(IReceiptItemService receiptitemService) : IRequestHandler<GetAllReceiptItemsQuery, PagedResult<Domain.Core.ReceiptItem>>
{
	public async ValueTask<PagedResult<Domain.Core.ReceiptItem>> Handle(GetAllReceiptItemsQuery request, CancellationToken cancellationToken)
	{
		return await receiptitemService.GetAllAsync(
			request.Offset,
			request.Limit,
			request.Sort,
			request.Q,
			request.NormalizedDescriptionId,
			cancellationToken);
	}
}
