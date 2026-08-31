using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.Receipt;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Receipt;

public class GetAllReceiptsQueryHandlerTests
{
	[Fact]
	public async Task Handle_ShouldReturnAllAccounts()
	{
		List<ReceiptListItem> expected = CreateListItems(2);

		Mock<IReceiptService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, null, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ReceiptListItem>(expected, expected.Count, 0, 50));

		GetAllReceiptsQueryHandler handler = new(mockService.Object);
		GetAllReceiptsQuery query = new(0, 50, SortParams.Default);

		PagedResult<ReceiptListItem> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
	}

	[Fact]
	public async Task Handle_ShouldPassAccountIdAndCardIdFilters()
	{
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		List<ReceiptListItem> expected = CreateListItems(1);

		Mock<IReceiptService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), accountId, cardId, null, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ReceiptListItem>(expected, expected.Count, 0, 50));

		GetAllReceiptsQueryHandler handler = new(mockService.Object);
		GetAllReceiptsQuery query = new(0, 50, SortParams.Default, accountId, cardId);

		PagedResult<ReceiptListItem> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.Verify(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), accountId, cardId, null, null, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ShouldPassSearchQueryToService()
	{
		List<ReceiptListItem> expected = CreateListItems(1);
		const string searchQuery = "Walmart";

		Mock<IReceiptService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, searchQuery, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ReceiptListItem>(expected, expected.Count, 0, 50));

		GetAllReceiptsQueryHandler handler = new(mockService.Object);
		GetAllReceiptsQuery query = new(0, 50, SortParams.Default, null, null, searchQuery);

		PagedResult<ReceiptListItem> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.Verify(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, searchQuery, null, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ShouldPassLocationFilterToService()
	{
		List<ReceiptListItem> expected = CreateListItems(1);
		const string location = "Target";

		Mock<IReceiptService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, null, location, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ReceiptListItem>(expected, expected.Count, 0, 50));

		GetAllReceiptsQueryHandler handler = new(mockService.Object);
		GetAllReceiptsQuery query = new(0, 50, SortParams.Default, null, null, null, location);

		PagedResult<ReceiptListItem> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.Verify(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, null, location, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ShouldPassLocationAndSearchQueryTogether()
	{
		List<ReceiptListItem> expected = CreateListItems(1);
		const string searchQuery = "Milk";
		const string location = "Target";

		Mock<IReceiptService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, searchQuery, location, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ReceiptListItem>(expected, expected.Count, 0, 50));

		GetAllReceiptsQueryHandler handler = new(mockService.Object);
		GetAllReceiptsQuery query = new(0, 50, SortParams.Default, null, null, searchQuery, location);

		PagedResult<ReceiptListItem> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.Verify(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, searchQuery, location, It.IsAny<CancellationToken>()), Times.Once);
	}

	private static List<ReceiptListItem> CreateListItems(int count) =>
		[.. Enumerable.Range(0, count).Select(index => new ReceiptListItem(
			Guid.NewGuid(), $"Location {index}", new DateOnly(2026, 8, 30), 1m,
			2m, 3m, 6m, 6m, "balanced", 1, "Food", "Checking · Visa"))];
}
