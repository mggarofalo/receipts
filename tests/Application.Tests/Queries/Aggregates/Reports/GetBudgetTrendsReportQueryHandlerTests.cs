using Application.Interfaces.Services;
using Application.Models.Reports;
using Application.Queries.Aggregates.Reports;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Aggregates.Reports;

public class GetBudgetTrendsReportQueryHandlerTests
{
	private readonly Mock<IReportService> _reportServiceMock;
	private readonly GetBudgetTrendsReportQueryHandler _handler;

	public GetBudgetTrendsReportQueryHandlerTests()
	{
		_reportServiceMock = new Mock<IReportService>();
		_handler = new GetBudgetTrendsReportQueryHandler(_reportServiceMock.Object);
	}

	[Fact]
	public async Task Handle_PassesAllParametersAndReturnsServiceResult()
	{
		// Arrange
		DateOnly start = new(2025, 1, 1);
		DateOnly end = new(2025, 6, 30);
		Guid envelopeId = Guid.NewGuid();
		Guid occasionId = Guid.NewGuid();
		using CancellationTokenSource cancellation = new();
		GetBudgetTrendsReportQuery query = new(start, end, "quarterly", 4, BudgetTrendsDimension.Occasion, envelopeId, occasionId);
		BudgetTrendsResult expected = new(BudgetTrendsDimension.Occasion,
			[new(occasionId, "Birthday", true, "USD", false, 12.5m, 2)],
			[new("2025 Q1", [12.5m])]);

		_reportServiceMock.Setup(s => s.GetBudgetTrendsAsync(start, end, "quarterly", 4,
			BudgetTrendsDimension.Occasion, envelopeId, occasionId, cancellation.Token))
			.ReturnsAsync(expected);

		// Act
		BudgetTrendsResult result = await _handler.Handle(query, cancellation.Token);

		// Assert
		result.Should().BeSameAs(expected);
		_reportServiceMock.Verify(s => s.GetBudgetTrendsAsync(start, end, "quarterly", 4,
			BudgetTrendsDimension.Occasion, envelopeId, occasionId, cancellation.Token), Times.Once);
	}

	[Fact]
	public async Task Handle_WithoutFilters_PassesNullFilters()
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 12, 31);
		GetBudgetTrendsReportQuery query = new(start, end, "monthly", 7, BudgetTrendsDimension.Envelope);
		_reportServiceMock.Setup(s => s.GetBudgetTrendsAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<string>(),
			It.IsAny<int>(), It.IsAny<BudgetTrendsDimension>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BudgetTrendsResult(BudgetTrendsDimension.Envelope, [], []));

		// Act
		await _handler.Handle(query, CancellationToken.None);

		// Assert
		_reportServiceMock.Verify(s => s.GetBudgetTrendsAsync(start, end, "monthly", 7,
			BudgetTrendsDimension.Envelope, null, null, It.IsAny<CancellationToken>()), Times.Once);
	}
}
