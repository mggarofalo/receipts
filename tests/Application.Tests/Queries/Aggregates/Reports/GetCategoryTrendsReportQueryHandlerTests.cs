using Application.Interfaces.Services;
using Application.Models.Reports;
using Application.Queries.Aggregates.Reports;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Aggregates.Reports;

public class GetCategoryTrendsReportQueryHandlerTests
{
	private readonly Mock<IReportService> _reportServiceMock;
	private readonly GetCategoryTrendsReportQueryHandler _handler;

	public GetCategoryTrendsReportQueryHandlerTests()
	{
		_reportServiceMock = new Mock<IReportService>();
		_handler = new GetCategoryTrendsReportQueryHandler(_reportServiceMock.Object);
	}

	[Fact]
	public async Task Handle_DelegatesToReportService()
	{
		// Arrange
		DateOnly start = new(2025, 1, 1);
		DateOnly end = new(2025, 12, 31);
		GetCategoryTrendsReportQuery query = new(start, end, "monthly", 7);
		CategoryTrendsResult expectedResult = new([], []);

		_reportServiceMock.Setup(s => s.GetCategoryTrendsAsync(
			start, end, "monthly", 7, It.IsAny<CancellationToken>(), It.IsAny<CategoryReportOptions>()))
			.ReturnsAsync(expectedResult);

		// Act
		CategoryTrendsResult result = await _handler.Handle(query, CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expectedResult);
		_reportServiceMock.Verify(s => s.GetCategoryTrendsAsync(
			start, end, "monthly", 7, It.IsAny<CancellationToken>(), It.IsAny<CategoryReportOptions>()), Times.Once);
	}

	[Fact]
	public async Task Handle_PassesAllParametersToService()
	{
		// Arrange
		DateOnly start = new(2024, 6, 1);
		DateOnly end = new(2024, 12, 31);
		GetCategoryTrendsReportQuery query = new(start, end, "quarterly", 5);
		CategoryTrendsResult expectedResult = new([], []);

		_reportServiceMock.Setup(s => s.GetCategoryTrendsAsync(
			start, end, "quarterly", 5, It.IsAny<CancellationToken>(), It.IsAny<CategoryReportOptions>()))
			.ReturnsAsync(expectedResult);

		// Act
		await _handler.Handle(query, CancellationToken.None);

		// Assert
		_reportServiceMock.Verify(s => s.GetCategoryTrendsAsync(
			start, end, "quarterly", 5, It.IsAny<CancellationToken>(), It.IsAny<CategoryReportOptions>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ReturnsServiceResult()
	{
		// Arrange
		DateOnly start = new(2025, 1, 1);
		DateOnly end = new(2025, 3, 31);
		GetCategoryTrendsReportQuery query = new(start, end, "monthly", 3);
		CategoryTrendsResult expectedResult = new(
			["Groceries", "Dining", "Other"],
			[
				new CategoryTrendsBucketResult("2025-01", [150.00m, 75.50m, 20.00m]),
				new CategoryTrendsBucketResult("2025-02", [200.00m, 60.00m, 15.00m]),
				new CategoryTrendsBucketResult("2025-03", [180.00m, 90.00m, 25.00m]),
			]);

		_reportServiceMock.Setup(s => s.GetCategoryTrendsAsync(
			It.IsAny<DateOnly>(), It.IsAny<DateOnly>(),
			It.IsAny<string>(), It.IsAny<int>(),
			It.IsAny<CancellationToken>(), It.IsAny<CategoryReportOptions>()))
			.ReturnsAsync(expectedResult);

		// Act
		CategoryTrendsResult result = await _handler.Handle(query, CancellationToken.None);

		// Assert
		result.Categories.Should().HaveCount(3);
		result.Categories.Should().ContainInOrder("Groceries", "Dining", "Other");
		result.Buckets.Should().HaveCount(3);
		result.Buckets[0].Period.Should().Be("2025-01");
		result.Buckets[0].Amounts.Should().Equal(150.00m, 75.50m, 20.00m);
		result.Buckets[2].Amounts.Should().Equal(180.00m, 90.00m, 25.00m);
	}
}
