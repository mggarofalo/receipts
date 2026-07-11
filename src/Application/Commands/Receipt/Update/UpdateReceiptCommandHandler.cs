using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Receipt.Update;

public class UpdateReceiptCommandHandler(IReceiptService receiptService) : IRequestHandler<UpdateReceiptCommand, bool>
{
	public async ValueTask<bool> Handle(UpdateReceiptCommand request, CancellationToken cancellationToken)
	{
		foreach (Domain.Core.Receipt receipt in request.Receipts)
		{
			bool exists = await receiptService.ExistsAsync(receipt.Id, cancellationToken);
			if (!exists)
			{
				return false;
			}
		}

		await receiptService.UpdateAsync([.. request.Receipts], cancellationToken);
		return true;
	}
}