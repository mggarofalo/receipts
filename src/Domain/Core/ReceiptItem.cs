using Domain.NormalizedDescriptions;

namespace Domain.Core;

public class ReceiptItem
{
	public Guid Id { get; set; }
	public Guid ReceiptId { get; set; }
	public Domain.Taxonomy.CurrentClassification? CurrentClassification { get; set; }
	public Guid NormalizationRevision { get; set; }
	public Domain.Taxonomy.ClassificationIntent? Classification { get; set; }
	public ReceiptNormalizationIntent? NormalizationIntent { get; set; }
	public NormalizationReviewState NormalizationReviewState { get; set; }
	public string? NormalizationReviewReason { get; set; }
	public string? NormalizationExclusionReason { get; set; }
	public Guid? NormalizationReviewDecisionId { get; set; }
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
	public const string UnitPriceMustBePositive = "Unit price must be positive";
	public const string TotalAmountExceedsTolerance = "Total amount must be within $0.01 of quantity times unit price";

	public ReceiptItem(Guid id, string? receiptItemCode, string description, decimal quantity, Money unitPrice, Money totalAmount, string category, string? subcategory)
	{
		Id = id;
		ReceiptItemCode = receiptItemCode;
		Description = description;
		Quantity = quantity;
		UnitPrice = unitPrice;
		TotalAmount = totalAmount;
		Category = category;
		Subcategory = subcategory;
		ValidateForWrite();
	}

	// Only persistence hydration may retain facts that predate current admission rules.
	// Infrastructure's write mapper validates the model again before creating an entity.
	internal static ReceiptItem FromStoredState(Guid id, string? receiptItemCode, string description,
		decimal quantity, Money unitPrice, Money totalAmount, string category, string? subcategory)
		=> new()
		{
			Id = id,
			ReceiptItemCode = receiptItemCode,
			Description = description,
			Quantity = quantity,
			UnitPrice = unitPrice,
			TotalAmount = totalAmount,
			Category = category,
			Subcategory = subcategory
		};

	private ReceiptItem()
	{
		Description = string.Empty;
		Category = string.Empty;
		UnitPrice = Money.Zero;
		TotalAmount = Money.Zero;
	}

	internal void ValidateForWrite()
		=> ValidateForWrite(Description, Quantity, UnitPrice, TotalAmount, Category);

	private static void ValidateForWrite(string description, decimal quantity, Money unitPrice,
		Money totalAmount, string category)
	{
		if (string.IsNullOrWhiteSpace(description))
		{
			throw new ArgumentException(DescriptionCannotBeEmpty, nameof(description));
		}

		if (quantity <= 0)
		{
			throw new ArgumentException(QuantityMustBePositive, nameof(quantity));
		}

		if (unitPrice.Amount <= 0)
		{
			throw new ArgumentException(UnitPriceMustBePositive, nameof(unitPrice));
		}

		decimal expectedTotal = Math.Round(quantity * unitPrice.Amount, 2, MidpointRounding.AwayFromZero);
		if (Math.Abs(totalAmount.Amount - expectedTotal) > 0.01m)
		{
			throw new ArgumentException(TotalAmountExceedsTolerance, nameof(totalAmount));
		}

		if (category is null)
		{
			throw new ArgumentException(CategoryCannotBeEmpty, nameof(category));
		}
	}
}
