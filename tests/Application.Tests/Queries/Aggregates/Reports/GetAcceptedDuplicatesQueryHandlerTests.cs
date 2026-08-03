using Application.Interfaces.Services;
using Application.Models.Reports;
using Application.Queries.Aggregates.Reports;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Aggregates.Reports;

public class GetAcceptedDuplicatesQueryHandlerTests
{
	private readonly Mock<IReportService> _reportServiceMock;
	private readonly GetAcceptedDuplicatesQueryHandler _handler;

	public GetAcceptedDuplicatesQueryHandlerTests()
	{
		_reportServiceMock = new Mock<IReportService>();
		_handler = new GetAcceptedDuplicatesQueryHandler(_reportServiceMock.Object);
	}

	[Fact]
	public async Task Handle_DelegatesToReportService()
	{
		// Arrange
		GetAcceptedDuplicatesQuery query = new();
		AcceptedDuplicatesResult expectedResult = new([], 0);

		_reportServiceMock.Setup(s => s.GetAcceptedDuplicatesAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(expectedResult);

		// Act
		AcceptedDuplicatesResult result = await _handler.Handle(query, CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expectedResult);
		_reportServiceMock.Verify(s => s.GetAcceptedDuplicatesAsync(
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ReturnsServiceResult()
	{
		// Arrange
		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		DateOnly date = new(2025, 7, 4);
		DateTimeOffset acceptedAt = new(2025, 7, 5, 12, 0, 0, TimeSpan.Zero);

		GetAcceptedDuplicatesQuery query = new();
		AcceptedDuplicatesResult expectedResult = new(
		[
			new AcceptedDuplicateGroup(
			[
				new DuplicateReceiptSummary(receiptA, "Store A", date, 42.99m),
				new DuplicateReceiptSummary(receiptB, "Store A", date, 42.99m),
			], [receiptA, receiptB], acceptedAt),
		], 1);

		_reportServiceMock.Setup(s => s.GetAcceptedDuplicatesAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(expectedResult);

		// Act
		AcceptedDuplicatesResult result = await _handler.Handle(query, CancellationToken.None);

		// Assert
		result.GroupCount.Should().Be(1);
		result.Groups.Should().ContainSingle();
		result.Groups[0].AcceptedAt.Should().Be(acceptedAt);
		result.Groups[0].Receipts.Select(r => r.ReceiptId).Should()
			.BeEquivalentTo(new[] { receiptA, receiptB });
	}

	[Fact]
	public async Task Handle_PassesCancellationTokenThrough()
	{
		// Arrange
		using CancellationTokenSource cts = new();
		GetAcceptedDuplicatesQuery query = new();

		_reportServiceMock.Setup(s => s.GetAcceptedDuplicatesAsync(cts.Token))
			.ReturnsAsync(new AcceptedDuplicatesResult([], 0));

		// Act
		await _handler.Handle(query, cts.Token);

		// Assert
		_reportServiceMock.Verify(s => s.GetAcceptedDuplicatesAsync(cts.Token), Times.Once);
	}
}
