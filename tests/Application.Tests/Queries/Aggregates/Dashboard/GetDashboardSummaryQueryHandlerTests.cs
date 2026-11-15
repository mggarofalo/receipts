using Application.Interfaces.Services;
using Application.Models.Dashboard;
using Application.Models.Reports;
using Application.Queries.Aggregates.Dashboard;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Aggregates.Dashboard;

public class GetDashboardSummaryQueryHandlerTests
{
	[Fact]
	public async Task Handle_ShouldReturnSummary()
	{
		// Arrange
		DateOnly startDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-30));
		DateOnly endDate = DateOnly.FromDateTime(DateTime.Today);

		DashboardSummaryResult expected = new(
			TotalReceipts: 5,
			TotalSpent: 250.50m,
			AverageTripAmount: 50.10m,
			MostUsedAccount: new NameCountResult("Visa", 3),
			MostUsedCategory: new NameCountResult("Groceries", 10));

		Mock<IDashboardService> mockService = new();
		mockService.Setup(s => s.GetSummaryAsync(startDate, endDate, CategoryReportView.AsClassified, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetDashboardSummaryQueryHandler handler = new(mockService.Object);
		GetDashboardSummaryQuery query = new(startDate, endDate, CategoryReportView.AsClassified);

		// Act
		DashboardSummaryResult result = await handler.Handle(query, CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expected);
		mockService.Verify(s => s.GetSummaryAsync(startDate, endDate, CategoryReportView.AsClassified, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ShouldPassDatesToService()
	{
		// Arrange
		DateOnly startDate = new(2025, 1, 1);
		DateOnly endDate = new(2025, 6, 30);

		DashboardSummaryResult expected = new(0, 0, 0, new NameCountResult(null, 0), new NameCountResult(null, 0));

		Mock<IDashboardService> mockService = new();
		mockService.Setup(s => s.GetSummaryAsync(startDate, endDate, CategoryReportView.AsClassified, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetDashboardSummaryQueryHandler handler = new(mockService.Object);
		GetDashboardSummaryQuery query = new(startDate, endDate, CategoryReportView.AsClassified);

		// Act
		await handler.Handle(query, CancellationToken.None);

		// Assert
		mockService.Verify(s => s.GetSummaryAsync(startDate, endDate, CategoryReportView.AsClassified, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ShouldPassViewToService()
	{
		// Arrange
		DateOnly startDate = new(2025, 1, 1);
		DateOnly endDate = new(2025, 6, 30);

		DashboardSummaryResult expected = new(0, 0, 0, new NameCountResult(null, 0), new NameCountResult(null, 0));

		Mock<IDashboardService> mockService = new();
		mockService.Setup(s => s.GetSummaryAsync(startDate, endDate, CategoryReportView.CurrentHierarchy, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetDashboardSummaryQueryHandler handler = new(mockService.Object);
		GetDashboardSummaryQuery query = new(startDate, endDate, CategoryReportView.CurrentHierarchy);

		// Act
		await handler.Handle(query, CancellationToken.None);

		// Assert
		mockService.Verify(s => s.GetSummaryAsync(startDate, endDate, CategoryReportView.CurrentHierarchy, It.IsAny<CancellationToken>()), Times.Once);
	}
}
