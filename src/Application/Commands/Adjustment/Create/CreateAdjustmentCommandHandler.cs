using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Adjustment.Create;

public class CreateAdjustmentCommandHandler(
	IAdjustmentService adjustmentService,
	IReceiptService receiptService) : IRequestHandler<CreateAdjustmentCommand, List<Domain.Core.Adjustment>>
{
	public async ValueTask<List<Domain.Core.Adjustment>> Handle(CreateAdjustmentCommand request, CancellationToken cancellationToken)
	{
		// Guard against creating a child under a missing or soft-deleted receipt (RECEIPTS-763).
		// ExistsAsync respects the soft-delete query filter, so a trashed receipt reads as absent
		// and we reject with 404 instead of orphaning an active adjustment under it (or letting a
		// nonexistent-id FK violation surface as a 500).
		if (!await receiptService.ExistsAsync(request.ReceiptId, cancellationToken))
		{
			throw new KeyNotFoundException($"Receipt {request.ReceiptId} not found.");
		}

		return await adjustmentService.CreateAsync([.. request.Adjustments], request.ReceiptId, cancellationToken);
	}
}
