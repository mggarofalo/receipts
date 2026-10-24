using Application.Interfaces;

namespace Application.Commands.ReceiptItem.Update;

public record UpdateReceiptItemCommand : ICommand<bool>
{
	public IReadOnlyList<Domain.Core.ReceiptItem> ReceiptItems { get; }
	public Guid? ClientRequestId { get; }

	public const string ReceiptItemsCannotBeEmptyExceptionMessage = "ReceiptItems list cannot be empty.";

	public UpdateReceiptItemCommand(List<Domain.Core.ReceiptItem> receiptItems, Guid? clientRequestId = null)
	{
		ArgumentNullException.ThrowIfNull(receiptItems);

		if (receiptItems.Count == 0)
		{
			throw new ArgumentException(ReceiptItemsCannotBeEmptyExceptionMessage, nameof(receiptItems));
		}

		ReceiptItems = receiptItems.AsReadOnly();
		ClientRequestId = clientRequestId;
	}
}
