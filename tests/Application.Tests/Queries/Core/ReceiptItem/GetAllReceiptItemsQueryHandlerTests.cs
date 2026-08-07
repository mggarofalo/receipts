using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.ReceiptItem;
using FluentAssertions;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Queries.Core.ReceiptItem;

public class GetAllReceiptItemsQueryHandlerTests
{
	[Fact]
	public async Task Handle_ShouldReturnAllReceiptItems()
	{
		List<Domain.Core.ReceiptItem> expected = ReceiptItemGenerator.GenerateList(2);

		Mock<IReceiptItemService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, It.IsAny<CancellationToken>())).ReturnsAsync(new PagedResult<Domain.Core.ReceiptItem>(expected, expected.Count, 0, 50));

		GetAllReceiptItemsQueryHandler handler = new(mockService.Object);
		GetAllReceiptItemsQuery query = new(0, 50, SortParams.Default);

		PagedResult<Domain.Core.ReceiptItem> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
	}

	// RECEIPTS-877: the normalized-description filter has to survive the trip through the handler,
	// or the split dialog silently gets the whole table back.
	[Fact]
	public async Task Handle_ShouldPassNormalizedDescriptionIdToService()
	{
		List<Domain.Core.ReceiptItem> expected = ReceiptItemGenerator.GenerateList(1);
		Guid normalizedDescriptionId = Guid.NewGuid();

		Mock<IReceiptItemService> mockService = new();
		mockService
			.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, normalizedDescriptionId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.ReceiptItem>(expected, expected.Count, 0, 50));

		GetAllReceiptItemsQueryHandler handler = new(mockService.Object);
		GetAllReceiptItemsQuery query = new(0, 50, SortParams.Default, null, normalizedDescriptionId);

		PagedResult<Domain.Core.ReceiptItem> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.Verify(
			r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, normalizedDescriptionId, It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task Handle_ShouldPassSearchQueryToService()
	{
		List<Domain.Core.ReceiptItem> expected = ReceiptItemGenerator.GenerateList(1);
		const string searchQuery = "Apples";

		Mock<IReceiptItemService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), searchQuery, null, It.IsAny<CancellationToken>())).ReturnsAsync(new PagedResult<Domain.Core.ReceiptItem>(expected, expected.Count, 0, 50));

		GetAllReceiptItemsQueryHandler handler = new(mockService.Object);
		GetAllReceiptItemsQuery query = new(0, 50, SortParams.Default, searchQuery);

		PagedResult<Domain.Core.ReceiptItem> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.Verify(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), searchQuery, null, It.IsAny<CancellationToken>()), Times.Once);
	}
}
