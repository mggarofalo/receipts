using API.Controllers.Aggregates;
using API.Generated.Dtos;
using Application.Commands.Reports;
using Application.Queries.Aggregates.Reports;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using AppReports = Application.Models.Reports;

namespace Presentation.API.Tests.Controllers.Aggregates;

public class ReportsControllerTests
{
	private readonly Mock<IMediator> _mediatorMock;
	private readonly ReportsController _controller;

	public ReportsControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_controller = new ReportsController(_mediatorMock.Object);
	}

	[Fact]
	public async Task GetOutOfBalance_ReturnsOkResult_WithDefaultParameters()
	{
		// Arrange
		AppReports.OutOfBalanceResult reportResult = new(
		[
			new AppReports.OutOfBalanceItem(
				Guid.NewGuid(), "Store A", new DateOnly(2025, 3, 1),
				10.00m, 1.00m, 0m, 11.00m, 15.00m, -4.00m),
		], 1, 4.00m);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetOutOfBalanceReportQuery>(q =>
				q.SortBy == "date" && q.SortDirection == "asc" && q.Page == 1 && q.PageSize == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<OutOfBalanceResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetOutOfBalance(null, null, null, null, CancellationToken.None);

		// Assert
		Ok<OutOfBalanceResponse> okResult = Assert.IsType<Ok<OutOfBalanceResponse>>(result.Result);
		OutOfBalanceResponse response = okResult.Value!;
		response.TotalCount.Should().Be(1);
		response.TotalDiscrepancy.Should().Be(4.00);
		response.Items.Should().ContainSingle();
		response.Items.First().Location.Should().Be("Store A");
		response.Items.First().Difference.Should().Be(-4.00);
	}

	[Fact]
	public async Task GetOutOfBalance_PassesCustomParameters()
	{
		// Arrange
		AppReports.OutOfBalanceResult reportResult = new([], 0, 0m);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetOutOfBalanceReportQuery>(q =>
				q.SortBy == "difference" && q.SortDirection == "desc" && q.Page == 2 && q.PageSize == 25),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<OutOfBalanceResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetOutOfBalance("difference", "desc", 2, 25, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<OutOfBalanceResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetOutOfBalanceReportQuery>(q =>
				q.SortBy == "difference" && q.SortDirection == "desc" && q.Page == 2 && q.PageSize == 25),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetOutOfBalance_ReturnsBadRequest_WhenInvalidSortBy()
	{
		// Act
		Results<Ok<OutOfBalanceResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetOutOfBalance("invalid", null, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("Invalid sortBy");
	}

	[Fact]
	public async Task GetOutOfBalance_ReturnsBadRequest_WhenInvalidSortDirection()
	{
		// Act
		Results<Ok<OutOfBalanceResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetOutOfBalance(null, "invalid", null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("Invalid sortDirection");
	}

	[Fact]
	public async Task GetOutOfBalance_ReturnsBadRequest_WhenPageLessThanOne()
	{
		// Act
		Results<Ok<OutOfBalanceResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetOutOfBalance(null, null, 0, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Be("page must be at least 1");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(101)]
	public async Task GetOutOfBalance_ReturnsBadRequest_WhenPageSizeOutOfRange(int pageSize)
	{
		// Act
		Results<Ok<OutOfBalanceResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetOutOfBalance(null, null, null, pageSize, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Be("pageSize must be between 1 and 100");
	}

	[Theory]
	[InlineData("date")]
	[InlineData("difference")]
	public async Task GetOutOfBalance_AcceptsValidSortColumns(string sortBy)
	{
		// Arrange
		AppReports.OutOfBalanceResult reportResult = new([], 0, 0m);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetOutOfBalanceReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<OutOfBalanceResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetOutOfBalance(sortBy, null, null, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<OutOfBalanceResponse>>(result.Result);
	}

	[Fact]
	public async Task GetOutOfBalance_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetOutOfBalanceReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetOutOfBalance(null, null, null, null, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetOutOfBalance_MapsResponseFieldsCorrectly()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		DateOnly date = new(2025, 6, 15);

		AppReports.OutOfBalanceResult reportResult = new(
		[
			new AppReports.OutOfBalanceItem(
				receiptId, "Test Location", date,
				25.50m, 2.25m, 1.00m, 28.75m, 30.00m, -1.25m),
		], 1, 1.25m);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetOutOfBalanceReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<OutOfBalanceResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetOutOfBalance(null, null, null, null, CancellationToken.None);

		// Assert
		Ok<OutOfBalanceResponse> okResult = Assert.IsType<Ok<OutOfBalanceResponse>>(result.Result);
		OutOfBalanceItem item = okResult.Value!.Items.First();
		item.ReceiptId.Should().Be(receiptId);
		item.Location.Should().Be("Test Location");
		item.Date.Should().Be(date);
		item.ItemSubtotal.Should().Be(25.50);
		item.TaxAmount.Should().Be(2.25);
		item.AdjustmentTotal.Should().Be(1.00);
		item.ExpectedTotal.Should().Be(28.75);
		item.TransactionTotal.Should().Be(30.00);
		item.Difference.Should().Be(-1.25);
	}

	// ── GetItemDescriptions ──────────────────────────────

	[Fact]
	public async Task GetItemDescriptions_ReturnsOkResult_WithValidSearch()
	{
		// Arrange
		AppReports.ItemDescriptionResult descResult = new(
		[
			new AppReports.ItemDescriptionItem("Milk", "Dairy", 10),
			new AppReports.ItemDescriptionItem("Milk Chocolate", "Candy", 3),
		]);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetItemDescriptionsQuery>(q =>
				q.Search == "mi" && !q.CategoryOnly && q.Limit == 20),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(descResult);

		// Act
		Results<Ok<ItemDescriptionsResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemDescriptions("mi", null, null, CancellationToken.None);

		// Assert
		Ok<ItemDescriptionsResponse> okResult = Assert.IsType<Ok<ItemDescriptionsResponse>>(result.Result);
		okResult.Value!.Items.Should().HaveCount(2);
		okResult.Value!.Items.First().Description.Should().Be("Milk");
		okResult.Value!.Items.First().Category.Should().Be("Dairy");
		okResult.Value!.Items.First().Occurrences.Should().Be(10);
	}

	[Fact]
	public async Task GetItemDescriptions_PassesCategoryOnlyAndLimit()
	{
		// Arrange
		AppReports.ItemDescriptionResult descResult = new([]);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetItemDescriptionsQuery>(q =>
				q.Search == "da" && q.CategoryOnly && q.Limit == 10),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(descResult);

		// Act
		Results<Ok<ItemDescriptionsResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemDescriptions("da", true, 10, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<ItemDescriptionsResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetItemDescriptionsQuery>(q =>
				q.Search == "da" && q.CategoryOnly && q.Limit == 10),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("a")]
	public async Task GetItemDescriptions_ReturnsBadRequest_WhenSearchTooShort(string? search)
	{
		// Act
		Results<Ok<ItemDescriptionsResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemDescriptions(search, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("search must be at least 2 characters");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(51)]
	public async Task GetItemDescriptions_ReturnsBadRequest_WhenLimitOutOfRange(int limit)
	{
		// Act
		Results<Ok<ItemDescriptionsResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemDescriptions("milk", null, limit, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("limit must be between 1 and 50");
	}

	// ── GetItemCostOverTime ──────────────────────────────

	[Fact]
	public async Task GetItemCostOverTime_ReturnsOkResult_WithDescription()
	{
		// Arrange
		AppReports.ItemCostOverTimeResult costResult = new(
		[
			new AppReports.ItemCostBucket("2025-01-15", 3.99m),
			new AppReports.ItemCostBucket("2025-02-20", 4.29m),
		]);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetItemCostOverTimeQuery>(q =>
				q.Description == "Milk" && q.Category == null && q.Granularity == "exact"),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(costResult);

		// Act
		Results<Ok<ItemCostOverTimeResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemCostOverTime("Milk", null, null, null, null, null, CancellationToken.None);

		// Assert
		Ok<ItemCostOverTimeResponse> okResult = Assert.IsType<Ok<ItemCostOverTimeResponse>>(result.Result);
		okResult.Value!.Buckets.Should().HaveCount(2);
		okResult.Value!.Buckets.First().Period.Should().Be("2025-01-15");
		okResult.Value!.Buckets.First().Amount.Should().Be(3.99);
	}

	[Fact]
	public async Task GetItemCostOverTime_ReturnsOkResult_WithCategory()
	{
		// Arrange
		AppReports.ItemCostOverTimeResult costResult = new([]);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetItemCostOverTimeQuery>(q =>
				q.Description == null && q.Category == "Dairy" && q.Granularity == "monthly"),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(costResult);

		// Act
		Results<Ok<ItemCostOverTimeResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemCostOverTime(null, "Dairy", null, null, "monthly", null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<ItemCostOverTimeResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetItemCostOverTimeQuery>(q =>
				q.Category == "Dairy" && q.Granularity == "monthly"),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetItemCostOverTime_ReturnsBadRequest_WhenNoDescriptionCategoryOrNormalizedDescription()
	{
		// Act
		Results<Ok<ItemCostOverTimeResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemCostOverTime(null, null, null, null, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("One of description, normalizedDescription, or category is required");
	}

	[Fact]
	public async Task GetItemCostOverTime_ReturnsOkResult_WithNormalizedDescriptionAlone()
	{
		// Arrange
		AppReports.ItemCostOverTimeResult costResult = new(
		[
			new AppReports.ItemCostBucket("2025-01-15", 3.99m),
		]);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetItemCostOverTimeQuery>(q =>
				q.Description == null && q.Category == null && q.NormalizedDescription == "Milk"),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(costResult);

		// Act
		Results<Ok<ItemCostOverTimeResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemCostOverTime(null, null, null, null, null, "Milk", CancellationToken.None);

		// Assert
		Ok<ItemCostOverTimeResponse> okResult = Assert.IsType<Ok<ItemCostOverTimeResponse>>(result.Result);
		okResult.Value!.Buckets.Should().ContainSingle();
	}

	[Fact]
	public async Task GetItemCostOverTime_ForwardsNormalizedDescriptionIntoQuery()
	{
		// Arrange
		AppReports.ItemCostOverTimeResult costResult = new([]);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetItemCostOverTimeQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(costResult);

		// Act
		await _controller.GetItemCostOverTime(null, null, null, null, null, "Organic Milk", CancellationToken.None);

		// Assert
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetItemCostOverTimeQuery>(q => q.NormalizedDescription == "Organic Milk"),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetItemCostOverTime_ReturnsBadRequest_WhenInvalidGranularity()
	{
		// Act
		Results<Ok<ItemCostOverTimeResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemCostOverTime("Milk", null, null, null, "invalid", null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("Invalid granularity");
	}

	[Theory]
	[InlineData("exact")]
	[InlineData("monthly")]
	[InlineData("yearly")]
	public async Task GetItemCostOverTime_AcceptsValidGranularities(string granularity)
	{
		// Arrange
		AppReports.ItemCostOverTimeResult costResult = new([]);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetItemCostOverTimeQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(costResult);

		// Act
		Results<Ok<ItemCostOverTimeResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemCostOverTime("Milk", null, null, null, granularity, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<ItemCostOverTimeResponse>>(result.Result);
	}

	[Fact]
	public async Task GetItemCostOverTime_ReturnsBadRequest_WhenStartDateAfterEndDate()
	{
		// Act
		Results<Ok<ItemCostOverTimeResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemCostOverTime("Milk", null, new DateOnly(2025, 12, 31), new DateOnly(2025, 1, 1), null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("startDate must be before or equal to endDate");
	}

	[Fact]
	public async Task GetItemCostOverTime_PassesDateRange()
	{
		// Arrange
		DateOnly start = new(2025, 1, 1);
		DateOnly end = new(2025, 12, 31);
		AppReports.ItemCostOverTimeResult costResult = new([]);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetItemCostOverTimeQuery>(q =>
				q.StartDate == start && q.EndDate == end),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(costResult);

		// Act
		Results<Ok<ItemCostOverTimeResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetItemCostOverTime("Milk", null, start, end, null, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<ItemCostOverTimeResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetItemCostOverTimeQuery>(q =>
				q.StartDate == start && q.EndDate == end),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	// ── CategoryTrends ─────────────────────────────

	[Fact]
	public async Task GetCategoryTrends_ReturnsOkResult_WithDefaultParameters()
	{
		// Arrange
		AppReports.CategoryTrendsResult trendsResult = new(
			["Groceries", "Dining"],
			[new AppReports.CategoryTrendsBucketResult("2025-01", [100.00m, 50.00m])]);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetCategoryTrendsReportQuery>(q =>
				q.Granularity == "monthly" && q.TopN == 7),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(trendsResult);

		// Act
		Results<Ok<CategoryTrendsResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetCategoryTrends(null, null, null, null, CancellationToken.None);

		// Assert
		Ok<CategoryTrendsResponse> okResult = Assert.IsType<Ok<CategoryTrendsResponse>>(result.Result);
		CategoryTrendsResponse response = okResult.Value!;
		response.Categories.Should().ContainInOrder("Groceries", "Dining");
		response.Buckets.Should().ContainSingle();
		response.Buckets.First().Period.Should().Be("2025-01");
		response.Buckets.First().Amounts.Should().Equal(100.00, 50.00);
	}

	[Fact]
	public async Task GetCategoryTrends_ReturnsBadRequest_WhenStartDateAfterEndDate()
	{
		// Act
		Results<Ok<CategoryTrendsResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetCategoryTrends(new DateOnly(2025, 12, 31), new DateOnly(2025, 1, 1), null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("startDate must be before or equal to endDate");
	}

	[Fact]
	public async Task GetCategoryTrends_ReturnsBadRequest_WhenInvalidGranularity()
	{
		// Act
		Results<Ok<CategoryTrendsResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetCategoryTrends(null, null, "invalid", null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("Invalid granularity");
	}

	[Theory]
	[InlineData("daily")]
	[InlineData("monthly")]
	[InlineData("quarterly")]
	[InlineData("yearly")]
	public async Task GetCategoryTrends_AcceptsValidGranularities(string granularity)
	{
		// Arrange
		AppReports.CategoryTrendsResult trendsResult = new([], []);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetCategoryTrendsReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(trendsResult);

		// Act
		Results<Ok<CategoryTrendsResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetCategoryTrends(null, null, granularity, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<CategoryTrendsResponse>>(result.Result);
	}

	[Fact]
	public async Task GetCategoryTrends_ReturnsBadRequest_WhenTopNTooLow()
	{
		// Act
		Results<Ok<CategoryTrendsResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetCategoryTrends(null, null, null, 0, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("topN must be between 1 and 50");
	}

	[Fact]
	public async Task GetCategoryTrends_ReturnsBadRequest_WhenTopNTooHigh()
	{
		// Act
		Results<Ok<CategoryTrendsResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetCategoryTrends(null, null, null, 51, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("topN must be between 1 and 50");
	}

	[Fact]
	public async Task GetCategoryTrends_PassesCustomParameters()
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 12, 31);
		AppReports.CategoryTrendsResult trendsResult = new([], []);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetCategoryTrendsReportQuery>(q =>
				q.StartDate == start && q.EndDate == end && q.Granularity == "quarterly" && q.TopN == 5),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(trendsResult);

		// Act
		Results<Ok<CategoryTrendsResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetCategoryTrends(start, end, "quarterly", 5, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<CategoryTrendsResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetCategoryTrendsReportQuery>(q =>
				q.StartDate == start && q.EndDate == end && q.Granularity == "quarterly" && q.TopN == 5),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	// ── GetDuplicates ──────────────────────────────

	[Fact]
	public async Task GetDuplicates_ReturnsOkResult_WithDefaultParameters()
	{
		// Arrange
		AppReports.DuplicateDetectionResult reportResult = new([], 0, 0);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetDuplicateDetectionReportQuery>(q =>
				q.MatchOn == "dateAndLocation" && q.LocationTolerance == "exact" && q.TotalTolerance == 0m),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<DuplicatesResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetDuplicates(null, null, null, null, CancellationToken.None);

		// Assert
		Ok<DuplicatesResponse> okResult = Assert.IsType<Ok<DuplicatesResponse>>(result.Result);
		okResult.Value!.GroupCount.Should().Be(0);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetDuplicateDetectionReportQuery>(q =>
				q.MatchOn == "dateAndLocation" && q.LocationTolerance == "exact" && q.TotalTolerance == 0m),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Theory]
	[InlineData("dateAndLocation")]
	[InlineData("dateAndTotal")]
	[InlineData("dateAndLocationAndTotal")]
	public async Task GetDuplicates_AcceptsValidMatchOnValues(string matchOn)
	{
		// Arrange
		AppReports.DuplicateDetectionResult reportResult = new([], 0, 0);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetDuplicateDetectionReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<DuplicatesResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetDuplicates(matchOn, null, null, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<DuplicatesResponse>>(result.Result);
	}

	[Theory]
	[InlineData("DateAndLocation")]
	[InlineData("DATEANDLOCATION")]
	[InlineData("DateAndTotal")]
	[InlineData("DateAndLocationAndTotal")]
	public async Task GetDuplicates_AcceptsMatchOnCaseInsensitively(string matchOn)
	{
		// Arrange
		AppReports.DuplicateDetectionResult reportResult = new([], 0, 0);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetDuplicateDetectionReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<DuplicatesResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetDuplicates(matchOn, null, null, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<DuplicatesResponse>>(result.Result);
	}

	[Fact]
	public async Task GetDuplicates_ReturnsBadRequest_WhenInvalidMatchOn()
	{
		// Act
		Results<Ok<DuplicatesResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetDuplicates("bogus", null, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("Invalid matchOn");
		badResult.Value!.Detail.Should().Contain("dateAndLocation");
	}

	[Fact]
	public async Task GetDuplicates_ReturnsBadRequest_WhenInvalidLocationTolerance()
	{
		// Act
		Results<Ok<DuplicatesResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetDuplicates(null, "fuzzy", null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("Invalid locationTolerance");
	}

	[Fact]
	public async Task GetDuplicates_ReturnsBadRequest_WhenNegativeTotalTolerance()
	{
		// Act
		Results<Ok<DuplicatesResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetDuplicates(null, null, -0.01, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Be("totalTolerance must be >= 0");
	}

	[Fact]
	public async Task GetDuplicates_PassesCustomParameters()
	{
		// Arrange
		AppReports.DuplicateDetectionResult reportResult = new([], 0, 0);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetDuplicateDetectionReportQuery>(q =>
				q.MatchOn == "dateAndLocationAndTotal" && q.LocationTolerance == "normalized" && q.TotalTolerance == 0.05m),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<DuplicatesResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetDuplicates("dateAndLocationAndTotal", "normalized", 0.05, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<DuplicatesResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetDuplicateDetectionReportQuery>(q =>
				q.MatchOn == "dateAndLocationAndTotal" && q.LocationTolerance == "normalized" && q.TotalTolerance == 0.05m),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetDuplicates_MapsResponseFieldsCorrectly()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		DateOnly date = new(2025, 7, 4);

		AppReports.DuplicateDetectionResult reportResult = new(
		[
			new AppReports.DuplicateGroup(
				"2025-07-04|Test Store",
				[new AppReports.DuplicateReceiptSummary(receiptId, "Test Store", date, 42.99m)]),
		], 1, 1);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetDuplicateDetectionReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<DuplicatesResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetDuplicates(null, null, null, null, CancellationToken.None);

		// Assert
		Ok<DuplicatesResponse> okResult = Assert.IsType<Ok<DuplicatesResponse>>(result.Result);
		DuplicatesResponse response = okResult.Value!;
		response.GroupCount.Should().Be(1);
		response.TotalDuplicateReceipts.Should().Be(1);
		response.Groups.Should().ContainSingle();

		DuplicateGroup group = response.Groups.First();
		group.MatchKey.Should().Be("2025-07-04|Test Store");
		group.Receipts.Should().ContainSingle();

		DuplicateReceipt receipt = group.Receipts.First();
		receipt.ReceiptId.Should().Be(receiptId);
		receipt.Location.Should().Be("Test Store");
		receipt.Date.Should().Be(date);
		receipt.TransactionTotal.Should().Be(42.99);
	}

	// ── Duplicate-group acceptance (RECEIPTS-834) ──────────────────

	[Fact]
	public async Task GetDuplicates_PassesIncludeAcceptedFalse_ByDefault()
	{
		// Arrange
		AppReports.DuplicateDetectionResult reportResult = new([], 0, 0);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetDuplicateDetectionReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<DuplicatesResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetDuplicates(null, null, null, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<DuplicatesResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetDuplicateDetectionReportQuery>(q => !q.IncludeAccepted),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetDuplicates_PassesIncludeAcceptedTrue_WhenRequested()
	{
		// Arrange
		AppReports.DuplicateDetectionResult reportResult = new([], 0, 0);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetDuplicateDetectionReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<DuplicatesResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetDuplicates(null, null, null, true, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<DuplicatesResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetDuplicateDetectionReportQuery>(q => q.IncludeAccepted),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetDuplicates_MapsIsAcceptedOntoEachGroup()
	{
		// Arrange
		Guid acceptedReceiptId = Guid.NewGuid();
		Guid openReceiptId = Guid.NewGuid();
		DateOnly date = new(2025, 7, 4);

		AppReports.DuplicateDetectionResult reportResult = new(
		[
			new AppReports.DuplicateGroup(
				"2025-07-04 @ Accepted Store",
				[new AppReports.DuplicateReceiptSummary(acceptedReceiptId, "Accepted Store", date, 42.99m)],
				IsAccepted: true),
			new AppReports.DuplicateGroup(
				"2025-07-04 @ Open Store",
				[new AppReports.DuplicateReceiptSummary(openReceiptId, "Open Store", date, 10.00m)]),
		], 2, 2);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetDuplicateDetectionReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<DuplicatesResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetDuplicates(null, null, null, true, CancellationToken.None);

		// Assert
		Ok<DuplicatesResponse> okResult = Assert.IsType<Ok<DuplicatesResponse>>(result.Result);
		DuplicatesResponse response = okResult.Value!;
		response.Groups.Should().HaveCount(2);

		DuplicateGroup accepted = response.Groups.Single(g => g.MatchKey == "2025-07-04 @ Accepted Store");
		accepted.IsAccepted.Should().BeTrue();

		DuplicateGroup open = response.Groups.Single(g => g.MatchKey == "2025-07-04 @ Open Store");
		open.IsAccepted.Should().BeFalse();
	}

	[Fact]
	public async Task GetAcceptedDuplicates_ReturnsOk_AndMapsGroupsAndReceipts()
	{
		// Arrange
		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		DateOnly date = new(2025, 7, 4);
		DateTimeOffset acceptedAt = new(2025, 7, 5, 12, 0, 0, TimeSpan.Zero);

		AppReports.AcceptedDuplicatesResult reportResult = new(
		[
			new AppReports.AcceptedDuplicateGroup(
			[
				new AppReports.DuplicateReceiptSummary(receiptA, "Test Store", date, 42.99m),
				new AppReports.DuplicateReceiptSummary(receiptB, "Test Store", date, 42.99m),
			], [receiptA, receiptB], acceptedAt),
		], 1);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetAcceptedDuplicatesQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Ok<AcceptedDuplicatesResponse> result = await _controller.GetAcceptedDuplicates(CancellationToken.None);

		// Assert
		AcceptedDuplicatesResponse response = result.Value!;
		response.GroupCount.Should().Be(1);
		response.Groups.Should().ContainSingle();

		AcceptedDuplicateGroup group = response.Groups.First();
		group.AcceptedAt.Should().Be(acceptedAt);
		group.Receipts.Should().HaveCount(2);

		DuplicateReceipt first = group.Receipts.First();
		first.ReceiptId.Should().Be(receiptA);
		first.Location.Should().Be("Test Store");
		first.Date.Should().Be(date);
		first.TransactionTotal.Should().Be(42.99);
	}

	[Fact]
	public async Task GetAcceptedDuplicates_ReturnsEmptyResponse_WhenNothingAccepted()
	{
		// Arrange
		AppReports.AcceptedDuplicatesResult reportResult = new([], 0);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetAcceptedDuplicatesQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Ok<AcceptedDuplicatesResponse> result = await _controller.GetAcceptedDuplicates(CancellationToken.None);

		// Assert
		result.Value!.GroupCount.Should().Be(0);
		result.Value!.Groups.Should().BeEmpty();
	}

	[Fact]
	public async Task AcceptDuplicateGroup_ReturnsOk_WithAcceptedPairCount()
	{
		// Arrange
		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		AcceptDuplicateGroupRequest request = new() { ReceiptIds = [receiptA, receiptB] };

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<AcceptDuplicateGroupCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(1);

		// Act
		Results<Ok<AcceptDuplicateGroupResponse>, NotFound<ProblemDetails>> result =
			await _controller.AcceptDuplicateGroup(request, CancellationToken.None);

		// Assert
		Ok<AcceptDuplicateGroupResponse> okResult = Assert.IsType<Ok<AcceptDuplicateGroupResponse>>(result.Result);
		okResult.Value!.AcceptedPairCount.Should().Be(1);
		_mediatorMock.Verify(m => m.Send(
			It.Is<AcceptDuplicateGroupCommand>(c =>
				c.ReceiptIds.Count == 2 && c.ReceiptIds.Contains(receiptA) && c.ReceiptIds.Contains(receiptB)),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task AcceptDuplicateGroup_DeduplicatesReceiptIds_BeforeDispatching()
	{
		// Arrange — the same ID repeated must not become an extra command entry.
		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		AcceptDuplicateGroupRequest request = new() { ReceiptIds = [receiptA, receiptB, receiptA] };

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<AcceptDuplicateGroupCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(1);

		// Act
		Results<Ok<AcceptDuplicateGroupResponse>, NotFound<ProblemDetails>> result =
			await _controller.AcceptDuplicateGroup(request, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<AcceptDuplicateGroupResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<AcceptDuplicateGroupCommand>(c => c.ReceiptIds.Count == 2),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task AcceptDuplicateGroup_ReturnsNotFound_WhenReceiptDoesNotExist()
	{
		// Arrange
		Guid receiptA = Guid.NewGuid();
		Guid missing = Guid.NewGuid();
		AcceptDuplicateGroupRequest request = new() { ReceiptIds = [receiptA, missing] };

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<AcceptDuplicateGroupCommand>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException($"Receipt(s) not found: {missing}"));

		// Act
		Results<Ok<AcceptDuplicateGroupResponse>, NotFound<ProblemDetails>> result =
			await _controller.AcceptDuplicateGroup(request, CancellationToken.None);

		// Assert
		NotFound<ProblemDetails> notFoundResult = Assert.IsType<NotFound<ProblemDetails>>(result.Result);
		notFoundResult.Value!.Detail.Should().Contain(missing.ToString());
	}

	[Fact]
	public async Task UnacceptDuplicateGroup_ReturnsOk_WithRemovedPairCount()
	{
		// Arrange
		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		UnacceptDuplicateGroupRequest request = new() { ReceiptIds = [receiptA, receiptB] };

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<UnacceptDuplicateGroupCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(1);

		// Act
		Ok<UnacceptDuplicateGroupResponse> result =
			await _controller.UnacceptDuplicateGroup(request, CancellationToken.None);

		// Assert
		result.Value!.RemovedPairCount.Should().Be(1);
		_mediatorMock.Verify(m => m.Send(
			It.Is<UnacceptDuplicateGroupCommand>(c =>
				c.ReceiptIds.Count == 2 && c.ReceiptIds.Contains(receiptA) && c.ReceiptIds.Contains(receiptB)),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task UnacceptDuplicateGroup_DeduplicatesReceiptIds_BeforeDispatching()
	{
		// Arrange
		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		UnacceptDuplicateGroupRequest request = new() { ReceiptIds = [receiptA, receiptB, receiptB] };

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<UnacceptDuplicateGroupCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(1);

		// Act
		Ok<UnacceptDuplicateGroupResponse> result =
			await _controller.UnacceptDuplicateGroup(request, CancellationToken.None);

		// Assert
		result.Value.Should().NotBeNull();
		_mediatorMock.Verify(m => m.Send(
			It.Is<UnacceptDuplicateGroupCommand>(c => c.ReceiptIds.Count == 2),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	// ── GetSpendingByNormalizedDescription ─────────────────

	[Fact]
	public async Task GetSpendingByNormalizedDescription_ReturnsOkResult_WithDefaultParameters()
	{
		// Arrange
		AppReports.SpendingByNormalizedDescriptionResult reportResult = new([], 0, 0m, null, null);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSpendingByNormalizedDescriptionQuery>(q => q.From == null && q.To == null),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetSpendingByNormalizedDescription(null, null, null, null, null, null, CancellationToken.None);

		// Assert
		Ok<SpendingByNormalizedDescriptionResponse> okResult = Assert.IsType<Ok<SpendingByNormalizedDescriptionResponse>>(result.Result);
		okResult.Value!.Items.Should().BeEmpty();
		okResult.Value!.FromDate.Should().BeNull();
		okResult.Value!.ToDate.Should().BeNull();
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescription_UsesDefaultSortAndPagination_WhenParamsOmitted()
	{
		// Arrange
		AppReports.SpendingByNormalizedDescriptionResult reportResult = new([], 0, 0m, null, null);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSpendingByNormalizedDescriptionQuery>(q =>
				q.SortBy == "totalAmount" && q.SortDirection == "desc" && q.Page == 1 && q.PageSize == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetSpendingByNormalizedDescription(null, null, null, null, null, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<SpendingByNormalizedDescriptionResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetSpendingByNormalizedDescriptionQuery>(q =>
				q.SortBy == "totalAmount" && q.SortDirection == "desc" && q.Page == 1 && q.PageSize == 50),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescription_PassesCustomSortAndPaginationToMediator()
	{
		// Arrange
		AppReports.SpendingByNormalizedDescriptionResult reportResult = new([], 0, 0m, null, null);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSpendingByNormalizedDescriptionQuery>(q =>
				q.SortBy == "canonicalName" && q.SortDirection == "asc" && q.Page == 2 && q.PageSize == 10),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetSpendingByNormalizedDescription(null, null, "canonicalName", "asc", 2, 10, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<SpendingByNormalizedDescriptionResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetSpendingByNormalizedDescriptionQuery>(q =>
				q.SortBy == "canonicalName" && q.SortDirection == "asc" && q.Page == 2 && q.PageSize == 10),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Theory]
	[InlineData("canonicalName")]
	[InlineData("totalAmount")]
	[InlineData("itemCount")]
	public async Task GetSpendingByNormalizedDescription_AcceptsValidSortColumns(string sortBy)
	{
		// Arrange
		AppReports.SpendingByNormalizedDescriptionResult reportResult = new([], 0, 0m, null, null);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetSpendingByNormalizedDescriptionQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetSpendingByNormalizedDescription(null, null, sortBy, null, null, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<SpendingByNormalizedDescriptionResponse>>(result.Result);
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescription_ReturnsBadRequest_WhenInvalidSortBy()
	{
		// Act
		Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetSpendingByNormalizedDescription(null, null, "invalid", null, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("Invalid sortBy");
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescription_ReturnsBadRequest_WhenInvalidSortDirection()
	{
		// Act
		Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetSpendingByNormalizedDescription(null, null, null, "invalid", null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("Invalid sortDirection");
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescription_ReturnsBadRequest_WhenPageLessThanOne()
	{
		// Act
		Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetSpendingByNormalizedDescription(null, null, null, null, 0, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Be("page must be at least 1");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(101)]
	public async Task GetSpendingByNormalizedDescription_ReturnsBadRequest_WhenPageSizeOutOfRange(int pageSize)
	{
		// Act
		Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetSpendingByNormalizedDescription(null, null, null, null, null, pageSize, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Be("pageSize must be between 1 and 100");
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescription_PassesDateRangeToMediator()
	{
		// Arrange
		DateTimeOffset from = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
		DateTimeOffset to = new(2025, 12, 31, 0, 0, 0, TimeSpan.Zero);
		AppReports.SpendingByNormalizedDescriptionResult reportResult = new([], 0, 0m, from, to);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSpendingByNormalizedDescriptionQuery>(q => q.From == from && q.To == to),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetSpendingByNormalizedDescription(from, to, null, null, null, null, CancellationToken.None);

		// Assert
		Ok<SpendingByNormalizedDescriptionResponse> okResult = Assert.IsType<Ok<SpendingByNormalizedDescriptionResponse>>(result.Result);
		okResult.Value!.FromDate.Should().Be(from);
		okResult.Value!.ToDate.Should().Be(to);

		_mediatorMock.Verify(m => m.Send(
			It.Is<GetSpendingByNormalizedDescriptionQuery>(q => q.From == from && q.To == to),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescription_ReturnsBadRequest_WhenFromAfterTo()
	{
		// Arrange
		DateTimeOffset from = new(2025, 12, 31, 0, 0, 0, TimeSpan.Zero);
		DateTimeOffset to = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

		// Act
		Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetSpendingByNormalizedDescription(from, to, null, null, null, null, CancellationToken.None);

		// Assert
		BadRequest<ProblemDetails> badResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badResult.Value!.Detail.Should().Contain("from must be before or equal to to");
		_mediatorMock.Verify(
			m => m.Send(It.IsAny<GetSpendingByNormalizedDescriptionQuery>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescription_MapsResponseFieldsCorrectly()
	{
		// Arrange
		DateTimeOffset firstSeen = new(2025, 1, 5, 0, 0, 0, TimeSpan.Zero);
		DateTimeOffset lastSeen = new(2025, 11, 30, 0, 0, 0, TimeSpan.Zero);

		AppReports.SpendingByNormalizedDescriptionResult reportResult = new(
		[
			new AppReports.SpendingByNormalizedDescriptionItem("Organic Milk", 42.50m, "USD", 5, firstSeen, lastSeen, Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active),
			new AppReports.SpendingByNormalizedDescriptionItem("Oat Drink", 7.25m, "USD", 1, firstSeen, lastSeen, Domain.NormalizedDescriptions.NormalizedDescriptionStatus.PendingReview),
			new AppReports.SpendingByNormalizedDescriptionItem("(Not Normalized)", 12.00m, "USD", 2, null, null, null),
		], 3, 61.75m, null, null);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetSpendingByNormalizedDescriptionQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetSpendingByNormalizedDescription(null, null, null, null, null, null, CancellationToken.None);

		// Assert
		Ok<SpendingByNormalizedDescriptionResponse> okResult = Assert.IsType<Ok<SpendingByNormalizedDescriptionResponse>>(result.Result);
		SpendingByNormalizedDescriptionResponse response = okResult.Value!;
		response.Items.Should().HaveCount(3);
		response.TotalCount.Should().Be(3);
		response.GrandTotal.Should().Be(61.75);

		SpendingByNormalizedDescriptionItem first = response.Items.First(i => i.CanonicalName == "Organic Milk");
		first.TotalAmount.Should().Be(42.50);
		first.Currency.Should().Be("USD");
		first.ItemCount.Should().Be(5);
		first.FirstSeen.Should().Be(firstSeen);
		first.LastSeen.Should().Be(lastSeen);
		first.Status.Should().Be(NormalizedDescriptionStatus.Active);

		// The bucket a reviewer has not confirmed yet must arrive marked, or the client cannot
		// tell provisional money from settled money (RECEIPTS-875).
		SpendingByNormalizedDescriptionItem pending = response.Items.First(i => i.CanonicalName == "Oat Drink");
		pending.Status.Should().Be(NormalizedDescriptionStatus.PendingReview);

		SpendingByNormalizedDescriptionItem notNormalized = response.Items.First(i => i.CanonicalName == "(Not Normalized)");
		notNormalized.TotalAmount.Should().Be(12.00);
		notNormalized.ItemCount.Should().Be(2);
		notNormalized.FirstSeen.Should().BeNull();
		notNormalized.LastSeen.Should().BeNull();
		// No backing row, so no status to report — distinct from a row that happens to be Active.
		notNormalized.Status.Should().BeNull();
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescription_AcceptsOnlyFromSet()
	{
		// Arrange
		DateTimeOffset from = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
		AppReports.SpendingByNormalizedDescriptionResult reportResult = new([], 0, 0m, from, null);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSpendingByNormalizedDescriptionQuery>(q => q.From == from && q.To == null),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetSpendingByNormalizedDescription(from, null, null, null, null, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<SpendingByNormalizedDescriptionResponse>>(result.Result);
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescription_AcceptsOnlyToSet()
	{
		// Arrange
		DateTimeOffset to = new(2025, 12, 31, 0, 0, 0, TimeSpan.Zero);
		AppReports.SpendingByNormalizedDescriptionResult reportResult = new([], 0, 0m, null, to);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSpendingByNormalizedDescriptionQuery>(q => q.From == null && q.To == to),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequest<ProblemDetails>> result =
			await _controller.GetSpendingByNormalizedDescription(null, to, null, null, null, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<SpendingByNormalizedDescriptionResponse>>(result.Result);
	}

	[Fact]
	public void GetSpendingByNormalizedDescription_RequiresAuthorization()
	{
		// Assert — the ReportsController-level [Authorize] attribute applies to this action.
		System.Reflection.MethodInfo method = typeof(ReportsController)
			.GetMethod(nameof(ReportsController.GetSpendingByNormalizedDescription))!;

		bool classAttribute = typeof(ReportsController)
			.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
			.Length > 0;
		bool methodAllowsAnonymous = method
			.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true)
			.Length > 0;

		classAttribute.Should().BeTrue("ReportsController requires authentication at the class level");
		methodAllowsAnonymous.Should().BeFalse("GetSpendingByNormalizedDescription should not bypass authorization");
	}
}
