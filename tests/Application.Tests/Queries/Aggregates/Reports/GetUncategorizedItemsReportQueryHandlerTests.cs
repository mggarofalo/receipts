using Application.Interfaces.Services;
using Application.Models.Reports;
using Application.Queries.Aggregates.Reports;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Aggregates.Reports;

public class GetUncategorizedItemsReportQueryHandlerTests
{
	private readonly Mock<IReportService> _reportServiceMock;
	private readonly GetUncategorizedItemsReportQueryHandler _handler;

	public GetUncategorizedItemsReportQueryHandlerTests()
	{
		_reportServiceMock = new Mock<IReportService>();
		_handler = new GetUncategorizedItemsReportQueryHandler(_reportServiceMock.Object);
	}

	[Fact]
	public async Task Handle_DelegatesToReportService()
	{
		// Arrange
		GetUncategorizedItemsReportQuery query = new("description", "asc", 1, 50);
		UncategorizedItemsResult expectedResult = new([], 0);

		_reportServiceMock.Setup(s => s.GetUncategorizedItemsAsync(
			"description", "asc", 1, 50, "any", It.IsAny<CancellationToken>()))
			.ReturnsAsync(expectedResult);

		// Act
		UncategorizedItemsResult result = await _handler.Handle(query, CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expectedResult);
		_reportServiceMock.Verify(s => s.GetUncategorizedItemsAsync(
			"description", "asc", 1, 50, "any", It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_PassesAllParametersToService()
	{
		// Arrange
		GetUncategorizedItemsReportQuery query = new("total", "desc", 3, 25);
		UncategorizedItemsResult expectedResult = new([], 0);

		_reportServiceMock.Setup(s => s.GetUncategorizedItemsAsync(
			"total", "desc", 3, 25, "any", It.IsAny<CancellationToken>()))
			.ReturnsAsync(expectedResult);

		// Act
		await _handler.Handle(query, CancellationToken.None);

		// Assert
		_reportServiceMock.Verify(s => s.GetUncategorizedItemsAsync(
			"total", "desc", 3, 25, "any", It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ReturnsServiceResult()
	{
		// Arrange
		GetUncategorizedItemsReportQuery query = new("description", "asc", 1, 50);
		UncategorizedItemsResult expectedResult = new(
		[
			new UncategorizedItemRecord(Guid.NewGuid(), Guid.NewGuid(), "ABC",
				"Test Item", 1m, 5.00m, 5.00m, "Uncategorized", null),
		], 1);

		_reportServiceMock.Setup(s => s.GetUncategorizedItemsAsync(
			It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(expectedResult);

		// Act
		UncategorizedItemsResult result = await _handler.Handle(query, CancellationToken.None);

		// Assert
		result.TotalCount.Should().Be(1);
		result.Items.Should().ContainSingle();
	}

	[Fact]
	public async Task Handle_PassesSuggestionFilterToService()
	{
		// Arrange
		GetUncategorizedItemsReportQuery query = new("description", "asc", 1, 50, "with");
		UncategorizedItemsResult expectedResult = new([], 0);

		_reportServiceMock.Setup(s => s.GetUncategorizedItemsAsync(
			"description", "asc", 1, 50, "with", It.IsAny<CancellationToken>()))
			.ReturnsAsync(expectedResult);

		// Act
		UncategorizedItemsResult result = await _handler.Handle(query, CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expectedResult);
		_reportServiceMock.Verify(s => s.GetUncategorizedItemsAsync(
			"description", "asc", 1, 50, "with", It.IsAny<CancellationToken>()), Times.Once);
	}
}
