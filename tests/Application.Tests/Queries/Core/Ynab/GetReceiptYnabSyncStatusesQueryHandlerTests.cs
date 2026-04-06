using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetReceiptYnabSyncStatusesQueryHandlerTests
{
	[Fact]
	public async Task Handle_ReturnsStatusesFromService()
	{
		// Arrange
		Guid receiptId1 = Guid.NewGuid();
		Guid receiptId2 = Guid.NewGuid();
		List<Guid> receiptIds = [receiptId1, receiptId2];

		List<ReceiptYnabSyncStatusDto> expected =
		[
			new(receiptId1, ReceiptSyncStatusValue.Synced),
			new(receiptId2, ReceiptSyncStatusValue.NotSynced),
		];

		Mock<IYnabSyncRecordService> mockService = new();
		mockService.Setup(s => s.GetSyncStatusesByReceiptIdsAsync(receiptIds, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetReceiptYnabSyncStatusesQueryHandler handler = new(mockService.Object);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await handler.Handle(
			new GetReceiptYnabSyncStatusesQuery(receiptIds), CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expected);
	}

	[Fact]
	public async Task Handle_WithEmptyList_ReturnsEmptyResult()
	{
		// Arrange
		List<Guid> receiptIds = [];
		List<ReceiptYnabSyncStatusDto> expected = [];

		Mock<IYnabSyncRecordService> mockService = new();
		mockService.Setup(s => s.GetSyncStatusesByReceiptIdsAsync(receiptIds, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetReceiptYnabSyncStatusesQueryHandler handler = new(mockService.Object);

		// Act
		List<ReceiptYnabSyncStatusDto> result = await handler.Handle(
			new GetReceiptYnabSyncStatusesQuery(receiptIds), CancellationToken.None);

		// Assert
		result.Should().BeEmpty();
	}
}
