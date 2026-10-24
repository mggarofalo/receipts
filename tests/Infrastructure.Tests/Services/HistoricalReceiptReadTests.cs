using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Aggregates.ReceiptsWithItems;
using Domain.Aggregates;
using Domain.Core;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;
using Infrastructure.Services;
using Infrastructure.Tests.Mapping;
using Moq;

namespace Infrastructure.Tests.Services;

public class HistoricalReceiptReadTests
{
	[Theory]
	[InlineData("list")]
	[InlineData("by-id")]
	[InlineData("trash")]
	public async Task ItemRead_HistoricalMismatch_PreservesStoredFacts(string read)
	{
		// Arrange
		ReceiptItemEntity stored = ReceiptItemMapperTests.HistoricalItem();
		Mock<IReceiptItemRepository> repository = new();
		repository.Setup(value => value.GetByIdAsync(stored.Id, It.IsAny<CancellationToken>())).ReturnsAsync(stored);
		repository.Setup(value => value.GetCountAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(1);
		repository.Setup(value => value.GetAllAsync(0, 50, SortParams.Default, null, null,
			It.IsAny<CancellationToken>())).ReturnsAsync([stored]);
		repository.Setup(value => value.GetDeletedCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
		repository.Setup(value => value.GetDeletedAsync(0, 50, SortParams.Default,
			It.IsAny<CancellationToken>())).ReturnsAsync([stored]);
		ReceiptItemService service = new(repository.Object, new ReceiptItemMapper());

		// Act
		ReceiptItem? actual = read switch
		{
			"list" => Assert.Single((await service.GetAllAsync(0, 50, SortParams.Default, CancellationToken.None)).Data),
			"trash" => Assert.Single((await service.GetDeletedAsync(0, 50, SortParams.Default, CancellationToken.None)).Data),
			_ => await service.GetByIdAsync(stored.Id, CancellationToken.None)
		};

		// Assert
		Assert.NotNull(actual);
		Assert.Equal(stored.Id, actual.Id);
		Assert.Equal(stored.ReceiptId, actual.ReceiptId);
		Assert.Equal(stored.Description, actual.Description);
		Assert.Equal(stored.Quantity, actual.Quantity);
		Assert.Equal(stored.UnitPrice, actual.UnitPrice.Amount);
		Assert.Equal(stored.TotalAmount, actual.TotalAmount.Amount);
	}

	[Fact]
	public async Task ReceiptWithItemsRead_HistoricalMismatch_UsesExactStoredTotalAndDate()
	{
		// Arrange: this is the receipt read used by GetTripByReceiptIdQueryHandler.
		ReceiptItemEntity storedItem = ReceiptItemMapperTests.HistoricalItem();
		Guid receiptId = storedItem.ReceiptId;
		Receipt storedReceipt = new(receiptId, "Historical location", new DateOnly(2019, 3, 4), new(0.28m));
		Mock<IReceiptItemRepository> repository = new();
		repository.Setup(value => value.GetByReceiptIdCountAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(1);
		repository.Setup(value => value.GetByReceiptIdAsync(receiptId, 0, int.MaxValue, SortParams.Default,
			It.IsAny<CancellationToken>())).ReturnsAsync([storedItem]);
		ReceiptItemService items = new(repository.Object, new ReceiptItemMapper());
		Mock<IReceiptService> receipts = new();
		receipts.Setup(value => value.GetByIdAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(storedReceipt);
		Mock<IAdjustmentService> adjustments = new();
		adjustments.Setup(value => value.GetByReceiptIdAsync(receiptId, 0, int.MaxValue, SortParams.Default,
			It.IsAny<CancellationToken>())).ReturnsAsync(new PagedResult<Adjustment>([], 0, 0, int.MaxValue));
		GetReceiptWithItemsByReceiptIdQueryHandler handler = new(receipts.Object, items, adjustments.Object);

		// Act
		ReceiptWithItems? actual = await handler.Handle(new(receiptId), CancellationToken.None);

		// Assert
		Assert.NotNull(actual);
		Assert.Equal(new DateOnly(2019, 3, 4), actual.Receipt.Date);
		Assert.Equal(3.62m, actual.Subtotal.Amount);
		Assert.Equal(3.90m, actual.ExpectedTotal.Amount);
		ReceiptItem item = Assert.Single(actual.Items);
		Assert.Equal(storedItem.Description, item.Description);
		Assert.Equal(1.2345m, item.Quantity);
		Assert.Equal(3.1257m, item.UnitPrice.Amount);
		Assert.Equal(3.62m, item.TotalAmount.Amount);
	}
}
