namespace Application.Models.Search;

public record PurchaseSearchItem(
	Guid Id,
	Guid ReceiptId,
	DateOnly Date,
	string Location,
	string Description,
	string? ReceiptItemCode,
	Guid? NormalizedDescriptionId,
	string? NormalizedDescription,
	decimal Quantity,
	decimal UnitPrice,
	string UnitPriceCurrency,
	decimal TotalAmount,
	string TotalAmountCurrency);
