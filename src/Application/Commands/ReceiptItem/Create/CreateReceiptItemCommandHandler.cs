using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.ReceiptItem.Create;

public class CreateReceiptItemCommandHandler(
	IReceiptItemService receiptitemService,
	IReceiptService receiptService) : IRequestHandler<CreateReceiptItemCommand, List<Domain.Core.ReceiptItem>>
{
	public async ValueTask<List<Domain.Core.ReceiptItem>> Handle(CreateReceiptItemCommand request, CancellationToken cancellationToken)
	{
		// Guard against creating a child under a missing or soft-deleted receipt (RECEIPTS-763).
		// ExistsAsync respects the soft-delete query filter, so a trashed receipt reads as absent
		// and we reject with 404 instead of orphaning an active receipt item under it (or letting
		// a nonexistent-id FK violation surface as a 500).
		if (!await receiptService.ExistsAsync(request.ReceiptId, cancellationToken))
		{
			throw new KeyNotFoundException($"Receipt {request.ReceiptId} not found.");
		}

		return await receiptitemService.CreateAsync([.. request.ReceiptItems], request.ReceiptId, cancellationToken);
	}
}
