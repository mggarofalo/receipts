using Application.Interfaces;

namespace Application.Commands.Receipt.CreateComplete;

public record CreateCompleteReceiptCommand : ICommand<CreateCompleteReceiptResult>
{
	public Domain.Core.Receipt Receipt { get; }
	public IReadOnlyList<Domain.Core.Transaction> Transactions { get; }
	public IReadOnlyList<Domain.Core.ReceiptItem> Items { get; }
	public IReadOnlyList<Domain.Core.Adjustment> Adjustments { get; }

	public CreateCompleteReceiptCommand(
		Domain.Core.Receipt receipt,
		List<Domain.Core.Transaction> transactions,
		List<Domain.Core.ReceiptItem> items,
		List<Domain.Core.Adjustment>? adjustments = null)
	{
		ArgumentNullException.ThrowIfNull(receipt, nameof(receipt));
		ArgumentNullException.ThrowIfNull(transactions, nameof(transactions));
		ArgumentNullException.ThrowIfNull(items, nameof(items));
		adjustments ??= [];

		Receipt = receipt;
		Transactions = transactions.AsReadOnly();
		Items = items.AsReadOnly();
		Adjustments = adjustments.AsReadOnly();
	}
}
