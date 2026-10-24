using API.Generated.Dtos;
using API.Mapping.Aggregates;
using Common;
using Domain;
using Domain.Aggregates;
using Domain.Core;

namespace Presentation.API.Tests.Mapping.Aggregates;

public class ReceiptWithItemsMapperTests
{
	private readonly ReceiptWithItemsMapper _mapper = new();

	[Fact]
	public void ToResponse_MapsReceiptAndItems()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Guid item1Id = Guid.NewGuid();
		Guid item2Id = Guid.NewGuid();

		Receipt receipt = new(
			receiptId,
			"Supermarket",
			new DateOnly(2025, 3, 10),
			new Money(4.25m, Currency.USD)
		);

		ReceiptItem item1 = new(
			item1Id,
			"ITEM-A",
			"Milk",
			2.0m,
			new Money(3.99m, Currency.USD),
			new Money(7.98m, Currency.USD),
			"Dairy",
			"Beverages"
		);

		ReceiptItem item2 = new(
			item2Id,
			"ITEM-B",
			"Bread",
			1.0m,
			new Money(4.50m, Currency.USD),
			new Money(4.50m, Currency.USD),
			"Bakery",
			"Staples"
		);

		ReceiptWithItems aggregate = new()
		{
			Receipt = receipt,
			Items = [item1, item2],
			Adjustments = []
		};

		// Act
		ReceiptWithItemsResponse actual = _mapper.ToResponse(aggregate);

		// Assert — Receipt
		Assert.Equal(receiptId, actual.Receipt.Id);
		Assert.Equal("Supermarket", actual.Receipt.Location);
		Assert.Equal(new DateOnly(2025, 3, 10), actual.Receipt.Date);
		Assert.Equal((double)4.25m, actual.Receipt.TaxAmount);

		// Assert — Items
		Assert.Equal(2, actual.Items.Count);
		List<ReceiptItemResponse> items = [.. actual.Items];

		Assert.Equal(item1Id, items[0].Id);
		Assert.Equal("ITEM-A", items[0].ReceiptItemCode);
		Assert.Equal("Milk", items[0].Description);
		Assert.Equal((double)2.0m, items[0].Quantity);
		Assert.Equal((double)3.99m, items[0].UnitPrice);
		Assert.Equal("Dairy", items[0].Category);
		Assert.Equal("Beverages", items[0].Subcategory);

		Assert.Equal(item2Id, items[1].Id);
		Assert.Equal("ITEM-B", items[1].ReceiptItemCode);
		Assert.Equal("Bread", items[1].Description);
		Assert.Equal((double)1.0m, items[1].Quantity);
		Assert.Equal((double)4.50m, items[1].UnitPrice);
		Assert.Equal("Bakery", items[1].Category);
		Assert.Equal("Staples", items[1].Subcategory);
	}

	[Fact]
	public void ToResponse_MapsEmptyItemsList()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Receipt receipt = new(
			receiptId,
			"Empty Store",
			new DateOnly(2025, 1, 5),
			new Money(0.00m, Currency.USD)
		);

		ReceiptWithItems aggregate = new()
		{
			Receipt = receipt,
			Items = [],
			Adjustments = []
		};

		// Act
		ReceiptWithItemsResponse actual = _mapper.ToResponse(aggregate);

		// Assert
		Assert.Equal(receiptId, actual.Receipt.Id);
		Assert.Empty(actual.Items);
	}

	[Fact]
	public void ToResponse_CountsPersistedReviewStatesIndependentlyOfProductForeignKeys()
	{
		var states = new[] { Domain.NormalizedDescriptions.NormalizationReviewState.Verified,
						Domain.NormalizedDescriptions.NormalizationReviewState.NeedsReview,
						Domain.NormalizedDescriptions.NormalizationReviewState.Excluded };
		var aggregate = new ReceiptWithItems
		{
			Receipt = new Receipt(Guid.NewGuid(), "Store", new DateOnly(2025, 1, 1), new Money(0, Currency.USD)),
			Items = states.Select(state => new ReceiptItem(Guid.NewGuid(), null, "Raw", 1,
					new Money(2, Currency.USD), new Money(2, Currency.USD), "Food", null)
			{ NormalizationReviewState = state }).ToList(),
			Adjustments = []
		};
		var response = _mapper.ToResponse(aggregate);
		Assert.Equal(1, response.VerifiedItemCount);
		Assert.Equal(1, response.NeedsReviewItemCount);
		Assert.Equal(1, response.ExcludedItemCount);
		Assert.Equal(3, response.Items.Count);
	}
}
