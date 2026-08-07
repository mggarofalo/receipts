using Application.Interfaces;

namespace Application.Commands.ReceiptItem.Create;

public record CreateReceiptItemCommand : ICommand<List<Domain.Core.ReceiptItem>>
{
	public IReadOnlyList<Domain.Core.ReceiptItem> ReceiptItems { get; }
	public Guid ReceiptId { get; }

	/// <summary>
	/// Per-item template provenance, positionally aligned with <see cref="ReceiptItems"/>
	/// (RECEIPTS-881). Empty when no caller supplied any.
	/// </summary>
	/// <remarks>
	/// A parallel list rather than a field on <see cref="Domain.Core.ReceiptItem"/>: which template
	/// a line was typed from is an entry-time fact about the request, not a property of the stored
	/// item. Stamping it onto the domain model would imply it is persisted, and it is not — only
	/// the canonical description it resolves to is.
	///
	/// Parallel lists are a footgun, so a length mismatch throws at construction rather than
	/// silently stamping items with somebody else's template.
	/// </remarks>
	public IReadOnlyList<Guid?> ItemTemplateIds { get; }

	public const string ReceiptItemsListCannotBeEmpty = "Receipt items list cannot be empty.";
	public const string TemplateIdsMustAlignWithItems = "itemTemplateIds must have one entry per receipt item when supplied.";

	public CreateReceiptItemCommand(List<Domain.Core.ReceiptItem> receiptItems, Guid receiptId)
		: this(receiptItems, receiptId, itemTemplateIds: null)
	{
	}

	public CreateReceiptItemCommand(
		List<Domain.Core.ReceiptItem> receiptItems,
		Guid receiptId,
		List<Guid?>? itemTemplateIds)
	{
		ArgumentNullException.ThrowIfNull(receiptItems);

		if (receiptItems.Count == 0)
		{
			throw new ArgumentException(ReceiptItemsListCannotBeEmpty, nameof(receiptItems));
		}

		if (itemTemplateIds is not null && itemTemplateIds.Count != receiptItems.Count)
		{
			throw new ArgumentException(TemplateIdsMustAlignWithItems, nameof(itemTemplateIds));
		}

		ReceiptItems = receiptItems.AsReadOnly();
		ReceiptId = receiptId;
		ItemTemplateIds = itemTemplateIds?.AsReadOnly() ?? [];
	}
}
