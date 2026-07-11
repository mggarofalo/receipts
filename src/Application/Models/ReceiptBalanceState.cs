using Domain.Core;

namespace Application.Models;

/// <summary>
/// A consistent snapshot of a receipt and its children, read INSIDE the serialized
/// (row-locked) transaction that guards the receipt balance invariant. It is handed to
/// a validation delegate so the balance-equation check runs against a fresh view that
/// already reflects any concurrent write which committed first. See RECEIPTS-764.
/// </summary>
public sealed record ReceiptBalanceState
{
	public required Receipt Receipt { get; init; }
	public required IReadOnlyList<ReceiptItem> Items { get; init; }
	public required IReadOnlyList<Adjustment> Adjustments { get; init; }
	public required IReadOnlyList<Transaction> ExistingTransactions { get; init; }
}
