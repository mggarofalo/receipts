using Common;

namespace Domain.Core;

public class ReceiptItem
{
	public Guid Id { get; set; }
	public Guid ReceiptId { get; set; }
	public string? ReceiptItemCode { get; set; }
	public string Description { get; set; }
	public decimal Quantity { get; set; }
	public Money UnitPrice { get; set; }
	public Money TotalAmount { get; set; }
	// Category/Subcategory are stored as denormalized strings (not FK references).
	// This is intentional: values capture the historical categorization at time of entry,
	// while the Category/Subcategory tables serve as suggestion lists for the UI.
	public string Category { get; set; }
	public string? Subcategory { get; set; }
	public PricingMode PricingMode { get; set; }

	// Resolver-populated fields — readonly from a domain-logic perspective, but public setters
	// are required so Mapperly can populate them. Not included in the constructor: the
	// NormalizedDescription resolver (see RECEIPTS-578) writes the FK, the match score, and
	// read paths populate the denormalized name from the entity nav property.
	public Guid? NormalizedDescriptionId { get; set; }
	public string? NormalizedDescriptionName { get; set; }
	public double? NormalizedDescriptionMatchScore { get; set; }

	public const string DescriptionCannotBeEmpty = "Description cannot be empty";
	public const string QuantityMustBePositive = "Quantity must be positive";
	public const string CategoryCannotBeEmpty = "Category cannot be empty";
	public const string FlatPricingModeQuantityMustBeOne = "Quantity must be 1 when pricing mode is flat.";
	public const string UnitPriceMustBePositive = "Unit price must be positive";
	public const string UnitPriceMustBeNonNegative = "Unit price must be non-negative";
	public const string TotalAmountMustBePositive = "Total amount must be positive";
	public const string TotalAmountExceedsTolerance = "Total amount must be within $0.01 of quantity times unit price";

	public ReceiptItem(Guid id, string? receiptItemCode, string description, decimal quantity, Money unitPrice, Money totalAmount, string category, string? subcategory, PricingMode pricingMode = PricingMode.Quantity)
	{
		if (string.IsNullOrWhiteSpace(description))
		{
			throw new ArgumentException(DescriptionCannotBeEmpty, nameof(description));
		}

		if (quantity <= 0)
		{
			throw new ArgumentException(QuantityMustBePositive, nameof(quantity));
		}

		// Flat-priced items legitimately carry an unknown unitPrice (e.g. a Walmart
		// receipt prints only the line total for unit-priced items). The line total
		// is the source of truth in that case, so we relax the unit-price floor for
		// flat mode but still require a positive total.
		if (pricingMode == PricingMode.Flat)
		{
			if (unitPrice.Amount < 0)
			{
				throw new ArgumentException(UnitPriceMustBeNonNegative, nameof(unitPrice));
			}

			if (totalAmount.Amount <= 0)
			{
				throw new ArgumentException(TotalAmountMustBePositive, nameof(totalAmount));
			}
		}
		else
		{
			if (unitPrice.Amount <= 0)
			{
				throw new ArgumentException(UnitPriceMustBePositive, nameof(unitPrice));
			}

			decimal expectedTotal = Math.Floor(quantity * unitPrice.Amount * 100) / 100;
			if (Math.Abs(totalAmount.Amount - expectedTotal) > 0.01m)
			{
				throw new ArgumentException(TotalAmountExceedsTolerance, nameof(totalAmount));
			}
		}

		if (string.IsNullOrWhiteSpace(category))
		{
			throw new ArgumentException(CategoryCannotBeEmpty, nameof(category));
		}

		if (pricingMode == PricingMode.Flat && quantity != 1)
		{
			throw new ArgumentException(FlatPricingModeQuantityMustBeOne, nameof(quantity));
		}

		Id = id;
		ReceiptItemCode = receiptItemCode;
		Description = description;
		Quantity = quantity;
		UnitPrice = unitPrice;
		TotalAmount = totalAmount;
		Category = category;
		Subcategory = subcategory;
		PricingMode = pricingMode;
	}
}