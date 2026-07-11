using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.ReceiptItem.Update;

public class UpdateReceiptItemCommandHandler(IReceiptItemService receiptitemService) : IRequestHandler<UpdateReceiptItemCommand, bool>
{
	public async ValueTask<bool> Handle(UpdateReceiptItemCommand request, CancellationToken cancellationToken)
	{
		Domain.Core.ReceiptItem? existingItem = await receiptitemService.GetByIdAsync(request.ReceiptItems[0].Id, cancellationToken);
		if (existingItem is null)
		{
			return false;
		}

		await receiptitemService.UpdateAsync([.. request.ReceiptItems], existingItem.ReceiptId, cancellationToken);
		return true;
	}
}