using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.Receipt;
using FluentAssertions;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Queries.Core.Receipt;

public class GetAllReceiptsQueryHandlerTests
{
	[Fact]
	public async Task Handle_ShouldReturnAllAccounts()
	{
		List<Domain.Core.Receipt> expected = ReceiptGenerator.GenerateList(2);

		Mock<IReceiptService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, null, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.Receipt>(expected, expected.Count, 0, 50));

		GetAllReceiptsQueryHandler handler = new(mockService.Object);
		GetAllReceiptsQuery query = new(0, 50, SortParams.Default);

		PagedResult<Domain.Core.Receipt> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
	}

	[Fact]
	public async Task Handle_ShouldPassAccountIdAndCardIdFilters()
	{
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		List<Domain.Core.Receipt> expected = ReceiptGenerator.GenerateList(1);

		Mock<IReceiptService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), accountId, cardId, null, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.Receipt>(expected, expected.Count, 0, 50));

		GetAllReceiptsQueryHandler handler = new(mockService.Object);
		GetAllReceiptsQuery query = new(0, 50, SortParams.Default, accountId, cardId);

		PagedResult<Domain.Core.Receipt> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.Verify(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), accountId, cardId, null, null, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ShouldPassSearchQueryToService()
	{
		List<Domain.Core.Receipt> expected = ReceiptGenerator.GenerateList(1);
		const string searchQuery = "Walmart";

		Mock<IReceiptService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, searchQuery, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.Receipt>(expected, expected.Count, 0, 50));

		GetAllReceiptsQueryHandler handler = new(mockService.Object);
		GetAllReceiptsQuery query = new(0, 50, SortParams.Default, null, null, searchQuery);

		PagedResult<Domain.Core.Receipt> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.Verify(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, searchQuery, null, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ShouldPassLocationFilterToService()
	{
		List<Domain.Core.Receipt> expected = ReceiptGenerator.GenerateList(1);
		const string location = "Target";

		Mock<IReceiptService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, null, location, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.Receipt>(expected, expected.Count, 0, 50));

		GetAllReceiptsQueryHandler handler = new(mockService.Object);
		GetAllReceiptsQuery query = new(0, 50, SortParams.Default, null, null, null, location);

		PagedResult<Domain.Core.Receipt> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.Verify(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, null, location, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ShouldPassLocationAndSearchQueryTogether()
	{
		List<Domain.Core.Receipt> expected = ReceiptGenerator.GenerateList(1);
		const string searchQuery = "Milk";
		const string location = "Target";

		Mock<IReceiptService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, searchQuery, location, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.Receipt>(expected, expected.Count, 0, 50));

		GetAllReceiptsQueryHandler handler = new(mockService.Object);
		GetAllReceiptsQuery query = new(0, 50, SortParams.Default, null, null, searchQuery, location);

		PagedResult<Domain.Core.Receipt> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.Verify(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), null, null, searchQuery, location, It.IsAny<CancellationToken>()), Times.Once);
	}
}
