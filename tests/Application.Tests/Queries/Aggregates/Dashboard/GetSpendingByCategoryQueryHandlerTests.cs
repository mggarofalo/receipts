using Application.Interfaces.Services;
using Application.Models.Dashboard;
using Application.Queries.Aggregates.Dashboard;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Aggregates.Dashboard;

public class GetSpendingByCategoryQueryHandlerTests
{
	[Fact]
	public async Task Handle_ShouldReturnSpendingByCategory()
	{
		// Arrange
		DateOnly startDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-30));
		DateOnly endDate = DateOnly.FromDateTime(DateTime.Today);
		int limit = 10;

		SpendingByCategoryResult expected = new(
		[
			new SpendingCategoryItemResult("Groceries", 500.00m, 50.00m),
			new SpendingCategoryItemResult("Electronics", 300.00m, 30.00m),
			new SpendingCategoryItemResult("Dining", 200.00m, 20.00m)
		]);

		Mock<IDashboardService> mockService = new();
		mockService.Setup(s => s.GetSpendingByCategoryAsync(startDate, endDate, limit, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetSpendingByCategoryQueryHandler handler = new(mockService.Object);
		GetSpendingByCategoryQuery query = new(startDate, endDate, limit);

		// Act
		SpendingByCategoryResult result = await handler.Handle(query, CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expected);
		mockService.Verify(s => s.GetSpendingByCategoryAsync(startDate, endDate, limit, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ShouldPassLimitToService()
	{
		// Arrange
		DateOnly startDate = new(2025, 1, 1);
		DateOnly endDate = new(2025, 12, 31);
		int limit = 5;

		SpendingByCategoryResult expected = new([]);

		Mock<IDashboardService> mockService = new();
		mockService.Setup(s => s.GetSpendingByCategoryAsync(startDate, endDate, limit, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetSpendingByCategoryQueryHandler handler = new(mockService.Object);
		GetSpendingByCategoryQuery query = new(startDate, endDate, limit);

		// Act
		await handler.Handle(query, CancellationToken.None);

		// Assert
		mockService.Verify(s => s.GetSpendingByCategoryAsync(startDate, endDate, limit, It.IsAny<CancellationToken>()), Times.Once);
	}
	[Fact]
	public async Task Handle_ForwardsAllCategorySelectionOptions()
	{
		DateOnly start = new(2020, 1, 1);
		DateOnly end = new(2020, 12, 31);
		Application.Models.Reports.CategoryReportOptions options = new(
			Application.Models.Reports.CategoryReportView.CurrentHierarchy,
			Application.Models.Reports.CategoryReportRollup.Root, Guid.NewGuid(),
			DateTimeOffset.Parse("2026-10-01T00:00:00Z"), Guid.NewGuid());
		Mock<IDashboardService> service = new(MockBehavior.Strict);
		SpendingByCategoryResult expected = new([]);
		service.Setup(s => s.GetSpendingByCategoryAsync(start, end, 7, CancellationToken.None, options)).ReturnsAsync(expected);
		GetSpendingByCategoryQueryHandler handler = new(service.Object);
		(await handler.Handle(new(start, end, 7) { CategoryOptions = options }, CancellationToken.None)).Should().BeSameAs(expected);
		service.VerifyAll();
	}
}
