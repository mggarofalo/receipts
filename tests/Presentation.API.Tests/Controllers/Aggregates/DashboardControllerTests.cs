using API.Controllers.Aggregates;
using API.Generated.Dtos;
using API.Http;
using Application.Models.Dashboard;
using Application.Queries.Aggregates.Dashboard;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;

namespace Presentation.API.Tests.Controllers.Aggregates;

public class DashboardControllerTests
{
	private readonly Mock<IMediator> _mediatorMock;
	private readonly DashboardController _controller;

	public DashboardControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_controller = new DashboardController(_mediatorMock.Object);
	}

	#region GetDashboardSummary

	[Fact]
	public async Task GetDashboardSummary_ReturnsOkResult_WithValidDates()
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);
		DashboardSummaryResult summaryResult = new(
			TotalReceipts: 5,
			TotalSpent: 150.50m,
			AverageTripAmount: 30.10m,
			MostUsedAccount: new NameCountResult("Checking", 3),
			MostUsedCategory: new NameCountResult("Groceries", 4));

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetDashboardSummaryQuery>(q => q.StartDate == start && q.EndDate == end),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(summaryResult);

		// Act
		Results<Ok<DashboardSummaryResponse>, BadRequestProblem> result =
			await _controller.GetDashboardSummary(start, end, CancellationToken.None);

		// Assert
		Ok<DashboardSummaryResponse> okResult = Assert.IsType<Ok<DashboardSummaryResponse>>(result.Result);
		DashboardSummaryResponse response = okResult.Value!;
		response.TotalReceipts.Should().Be(5);
		response.TotalSpent.Should().Be(150.50);
		response.AverageTripAmount.Should().Be(30.10);
		response.MostUsedAccount.Name.Should().Be("Checking");
		response.MostUsedAccount.Count.Should().Be(3);
		response.MostUsedCategory.Name.Should().Be("Groceries");
		response.MostUsedCategory.Count.Should().Be(4);
	}

	[Fact]
	public async Task GetDashboardSummary_UsesDefaultDates_WhenNullDatesProvided()
	{
		// Arrange
		DateOnly today = DateOnly.FromDateTime(DateTime.Today);
		DateOnly expectedStart = DateOnly.MinValue;

		DashboardSummaryResult summaryResult = new(
			TotalReceipts: 0,
			TotalSpent: 0m,
			AverageTripAmount: 0m,
			MostUsedAccount: new NameCountResult(null, 0),
			MostUsedCategory: new NameCountResult(null, 0));

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetDashboardSummaryQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(summaryResult);

		// Act
		Results<Ok<DashboardSummaryResponse>, BadRequestProblem> result =
			await _controller.GetDashboardSummary(null, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<DashboardSummaryResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetDashboardSummaryQuery>(q =>
				q.StartDate == expectedStart
				&& q.EndDate == today),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetDashboardSummary_ReturnsBadRequest_WhenStartDateAfterEndDate()
	{
		// Arrange
		DateOnly start = new(2024, 2, 1);
		DateOnly end = new(2024, 1, 1);

		// Act
		Results<Ok<DashboardSummaryResponse>, BadRequestProblem> result =
			await _controller.GetDashboardSummary(start, end, CancellationToken.None);

		// Assert
		BadRequestProblem badResult = Assert.IsType<BadRequestProblem>(result.Result);
		badResult.Value!.Detail.Should().Be("startDate must be before or equal to endDate");
	}

	[Fact]
	public async Task GetDashboardSummary_ReturnsOk_WhenStartDateEqualsEndDate()
	{
		// Arrange
		DateOnly date = new(2024, 1, 15);
		DashboardSummaryResult summaryResult = new(
			TotalReceipts: 1,
			TotalSpent: 25m,
			AverageTripAmount: 25m,
			MostUsedAccount: new NameCountResult("Savings", 1),
			MostUsedCategory: new NameCountResult("Food", 1));

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetDashboardSummaryQuery>(q => q.StartDate == date && q.EndDate == date),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(summaryResult);

		// Act
		Results<Ok<DashboardSummaryResponse>, BadRequestProblem> result =
			await _controller.GetDashboardSummary(date, date, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<DashboardSummaryResponse>>(result.Result);
	}

	[Fact]
	public async Task GetDashboardSummary_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetDashboardSummaryQuery>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetDashboardSummary(start, end, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	#endregion

	#region GetSpendingOverTime

	[Fact]
	public async Task GetSpendingOverTime_ReturnsOkResult_WithValidParameters()
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 3, 31);
		SpendingOverTimeResult overTimeResult = new(
		[
			new SpendingBucketResult("2024-01", 100m),
			new SpendingBucketResult("2024-02", 200m),
			new SpendingBucketResult("2024-03", 150m),
		]);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSpendingOverTimeQuery>(q =>
				q.StartDate == start && q.EndDate == end && q.Granularity == "monthly"),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(overTimeResult);

		// Act
		Results<Ok<SpendingOverTimeResponse>, BadRequestProblem> result =
			await _controller.GetSpendingOverTime(start, end, "monthly", CancellationToken.None);

		// Assert
		Ok<SpendingOverTimeResponse> okResult = Assert.IsType<Ok<SpendingOverTimeResponse>>(result.Result);
		SpendingOverTimeResponse response = okResult.Value!;
		response.Buckets.Should().HaveCount(3);
		response.Buckets.First().Period.Should().Be("2024-01");
		response.Buckets.First().Amount.Should().Be(100);
	}

	[Fact]
	public async Task GetSpendingOverTime_UsesDefaultGranularity_WhenNullGranularity()
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);
		SpendingOverTimeResult overTimeResult = new([]);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSpendingOverTimeQuery>(q => q.Granularity == "monthly"),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(overTimeResult);

		// Act
		Results<Ok<SpendingOverTimeResponse>, BadRequestProblem> result =
			await _controller.GetSpendingOverTime(start, end, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<SpendingOverTimeResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetSpendingOverTimeQuery>(q => q.Granularity == "monthly"),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetSpendingOverTime_ReturnsBadRequest_WhenInvalidDates()
	{
		// Arrange
		DateOnly start = new(2024, 2, 1);
		DateOnly end = new(2024, 1, 1);

		// Act
		Results<Ok<SpendingOverTimeResponse>, BadRequestProblem> result =
			await _controller.GetSpendingOverTime(start, end, "monthly", CancellationToken.None);

		// Assert
		BadRequestProblem badResult = Assert.IsType<BadRequestProblem>(result.Result);
		badResult.Value!.Detail.Should().Be("startDate must be before or equal to endDate");
	}

	[Fact]
	public async Task GetSpendingOverTime_ReturnsBadRequest_WhenInvalidGranularity()
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);

		// Act
		Results<Ok<SpendingOverTimeResponse>, BadRequestProblem> result =
			await _controller.GetSpendingOverTime(start, end, "biweekly", CancellationToken.None);

		// Assert
		BadRequestProblem badResult = Assert.IsType<BadRequestProblem>(result.Result);
		badResult.Value!.Detail.Should().Contain("Invalid granularity");
	}

	[Theory]
	[InlineData("daily")]
	[InlineData("monthly")]
	[InlineData("quarterly")]
	[InlineData("yearly")]
	public async Task GetSpendingOverTime_AcceptsValidGranularities(string granularity)
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);
		SpendingOverTimeResult overTimeResult = new([]);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetSpendingOverTimeQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(overTimeResult);

		// Act
		Results<Ok<SpendingOverTimeResponse>, BadRequestProblem> result =
			await _controller.GetSpendingOverTime(start, end, granularity, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<SpendingOverTimeResponse>>(result.Result);
	}

	[Theory]
	[InlineData("Daily")]
	[InlineData("Monthly")]
	[InlineData("QUARTERLY")]
	[InlineData("YEARLY")]
	public async Task GetSpendingOverTime_AcceptsCaseInsensitiveGranularity(string granularity)
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);
		SpendingOverTimeResult overTimeResult = new([]);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetSpendingOverTimeQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(overTimeResult);

		// Act
		Results<Ok<SpendingOverTimeResponse>, BadRequestProblem> result =
			await _controller.GetSpendingOverTime(start, end, granularity, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<SpendingOverTimeResponse>>(result.Result);
	}

	[Fact]
	public async Task GetSpendingOverTime_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetSpendingOverTimeQuery>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetSpendingOverTime(start, end, "monthly", CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	#endregion

	#region GetEarliestReceiptYear

	[Fact]
	public async Task GetEarliestReceiptYear_ReturnsOkResult_WithYear()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetEarliestReceiptYearQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(2022);

		// Act
		Ok<EarliestReceiptYearResponse> result =
			await _controller.GetEarliestReceiptYear(CancellationToken.None);

		// Assert
		EarliestReceiptYearResponse response = result.Value!;
		response.Year.Should().Be(2022);
	}

	[Fact]
	public async Task GetEarliestReceiptYear_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetEarliestReceiptYearQuery>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetEarliestReceiptYear(CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	#endregion

	#region GetSpendingByCategory

	[Fact]
	public async Task GetSpendingByCategory_ReturnsOkResult_WithValidParameters()
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);
		SpendingByCategoryResult categoryResult = new(
		[
			new SpendingCategoryItemResult("Groceries", 100m, 50m),
			new SpendingCategoryItemResult("Gas", 100m, 50m),
		]);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSpendingByCategoryQuery>(q =>
				q.StartDate == start && q.EndDate == end && q.Limit == 10),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(categoryResult);

		// Act
		Results<Ok<SpendingByCategoryResponse>, BadRequestProblem> result =
			await _controller.GetSpendingByCategory(start, end, 10, CancellationToken.None);

		// Assert
		Ok<SpendingByCategoryResponse> okResult = Assert.IsType<Ok<SpendingByCategoryResponse>>(result.Result);
		SpendingByCategoryResponse response = okResult.Value!;
		response.Items.Should().HaveCount(2);
		response.Items.First().CategoryName.Should().Be("Groceries");
		response.Items.First().Amount.Should().Be(100);
		response.Items.First().Percentage.Should().Be(50);
	}

	[Fact]
	public async Task GetSpendingByCategory_ReturnsBadRequest_WhenInvalidDates()
	{
		// Arrange
		DateOnly start = new(2024, 2, 1);
		DateOnly end = new(2024, 1, 1);

		// Act
		Results<Ok<SpendingByCategoryResponse>, BadRequestProblem> result =
			await _controller.GetSpendingByCategory(start, end, 10, CancellationToken.None);

		// Assert
		BadRequestProblem badResult = Assert.IsType<BadRequestProblem>(result.Result);
		badResult.Value!.Detail.Should().Be("startDate must be before or equal to endDate");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(101)]
	public async Task GetSpendingByCategory_ReturnsBadRequest_WhenLimitOutOfRange(int limit)
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);

		// Act
		Results<Ok<SpendingByCategoryResponse>, BadRequestProblem> result =
			await _controller.GetSpendingByCategory(start, end, limit, CancellationToken.None);

		// Assert
		BadRequestProblem badResult = Assert.IsType<BadRequestProblem>(result.Result);
		badResult.Value!.Detail.Should().Be("limit must be between 1 and 100");
	}

	[Theory]
	[InlineData(1)]
	[InlineData(100)]
	public async Task GetSpendingByCategory_AcceptsBoundaryLimits(int limit)
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);
		SpendingByCategoryResult categoryResult = new([]);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSpendingByCategoryQuery>(q => q.Limit == limit),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(categoryResult);

		// Act
		Results<Ok<SpendingByCategoryResponse>, BadRequestProblem> result =
			await _controller.GetSpendingByCategory(start, end, limit, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<SpendingByCategoryResponse>>(result.Result);
	}

	[Fact]
	public async Task GetSpendingByCategory_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetSpendingByCategoryQuery>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetSpendingByCategory(start, end, 10, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	#endregion

	#region GetSpendingByAccount

	[Fact]
	public async Task GetSpendingByAccount_ReturnsOkResult_WithValidDates()
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);
		Guid accountId = Guid.NewGuid();
		SpendingByAccountResult accountResult = new(
		[
			new SpendingAccountItemResult(accountId, "Checking", 200m, 100m),
		]);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSpendingByAccountQuery>(q => q.StartDate == start && q.EndDate == end),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(accountResult);

		// Act
		Results<Ok<SpendingByAccountResponse>, BadRequestProblem> result =
			await _controller.GetSpendingByAccount(start, end, CancellationToken.None);

		// Assert
		Ok<SpendingByAccountResponse> okResult = Assert.IsType<Ok<SpendingByAccountResponse>>(result.Result);
		SpendingByAccountResponse response = okResult.Value!;
		response.Items.Should().HaveCount(1);
		response.Items.First().AccountId.Should().Be(accountId);
		response.Items.First().AccountName.Should().Be("Checking");
		response.Items.First().Amount.Should().Be(200);
		response.Items.First().Percentage.Should().Be(100);
	}

	[Fact]
	public async Task GetSpendingByAccount_ReturnsBadRequest_WhenInvalidDates()
	{
		// Arrange
		DateOnly start = new(2024, 2, 1);
		DateOnly end = new(2024, 1, 1);

		// Act
		Results<Ok<SpendingByAccountResponse>, BadRequestProblem> result =
			await _controller.GetSpendingByAccount(start, end, CancellationToken.None);

		// Assert
		BadRequestProblem badResult = Assert.IsType<BadRequestProblem>(result.Result);
		badResult.Value!.Detail.Should().Be("startDate must be before or equal to endDate");
	}

	[Fact]
	public async Task GetSpendingByAccount_UsesDefaultDates_WhenNullDatesProvided()
	{
		// Arrange
		DateOnly today = DateOnly.FromDateTime(DateTime.Today);
		DateOnly expectedStart = DateOnly.MinValue;

		SpendingByAccountResult accountResult = new([]);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetSpendingByAccountQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(accountResult);

		// Act
		Results<Ok<SpendingByAccountResponse>, BadRequestProblem> result =
			await _controller.GetSpendingByAccount(null, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<SpendingByAccountResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetSpendingByAccountQuery>(q =>
				q.StartDate == expectedStart
				&& q.EndDate == today),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetSpendingByAccount_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetSpendingByAccountQuery>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetSpendingByAccount(start, end, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	#endregion

	#region GetSpendingByStore

	[Fact]
	public async Task GetSpendingByStore_ReturnsOkResult_WithValidDates()
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);
		SpendingByStoreResult storeResult = new(
		[
			new SpendingByStoreItemResult("Walmart", 5, 250m, 50m),
			new SpendingByStoreItemResult("Target", 3, 150m, 50m),
		]);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetSpendingByStoreQuery>(q => q.StartDate == start && q.EndDate == end),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(storeResult);

		// Act
		Results<Ok<SpendingByStoreResponse>, BadRequestProblem> result =
			await _controller.GetSpendingByStore(start, end, CancellationToken.None);

		// Assert
		Ok<SpendingByStoreResponse> okResult = Assert.IsType<Ok<SpendingByStoreResponse>>(result.Result);
		SpendingByStoreResponse response = okResult.Value!;
		response.Items.Should().HaveCount(2);
		response.Items.First().Location.Should().Be("Walmart");
		response.Items.First().VisitCount.Should().Be(5);
		response.Items.First().TotalAmount.Should().Be(250);
		response.Items.First().AveragePerVisit.Should().Be(50);
	}

	[Fact]
	public async Task GetSpendingByStore_ReturnsBadRequest_WhenInvalidDates()
	{
		// Arrange
		DateOnly start = new(2024, 2, 1);
		DateOnly end = new(2024, 1, 1);

		// Act
		Results<Ok<SpendingByStoreResponse>, BadRequestProblem> result =
			await _controller.GetSpendingByStore(start, end, CancellationToken.None);

		// Assert
		BadRequestProblem badResult = Assert.IsType<BadRequestProblem>(result.Result);
		badResult.Value!.Detail.Should().Be("startDate must be before or equal to endDate");
	}

	[Fact]
	public async Task GetSpendingByStore_UsesDefaultDates_WhenNullDatesProvided()
	{
		// Arrange
		DateOnly today = DateOnly.FromDateTime(DateTime.Today);
		DateOnly expectedStart = DateOnly.MinValue;

		SpendingByStoreResult storeResult = new([]);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetSpendingByStoreQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(storeResult);

		// Act
		Results<Ok<SpendingByStoreResponse>, BadRequestProblem> result =
			await _controller.GetSpendingByStore(null, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<SpendingByStoreResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetSpendingByStoreQuery>(q =>
				q.StartDate == expectedStart
				&& q.EndDate == today),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetSpendingByStore_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		DateOnly start = new(2024, 1, 1);
		DateOnly end = new(2024, 1, 31);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetSpendingByStoreQuery>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetSpendingByStore(start, end, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	#endregion
	[Theory]
	[InlineData("bogus", null, null)]
	[InlineData("0", null, null)]
	[InlineData(null, "bogus", null)]
	[InlineData(null, null, "2026-10-01")]
	[InlineData(null, null, "2026-10-01T00:00:00+01:00")]
	[InlineData(null, null, "badZ")]
	public async Task CategorySelection_InvalidInput_ReturnsReasonedProblem(string? view, string? rollup, string? cutoff)
	{
		var result = await _controller.GetSpendingByCategory(null, null, categoryView: view, rollup: rollup, knowledgeCutoff: cutoff);
		Assert.IsType<BadRequestProblem>(result.Result).Value.Detail.Should().NotBeNullOrEmpty();
		_mediatorMock.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task CategorySelection_RejectsCapturedRevisionInHistoricalView()
	{
		var result = await _controller.GetSpendingByCategory(null, null, capturedCurrentRevisionId: Guid.NewGuid());
		Assert.IsType<BadRequestProblem>(result.Result).Value.Detail.Should().Contain("currentHierarchy");
		_mediatorMock.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task CategorySelection_MapsSnapshotAndStablePathToActualGeneratedDto()
	{
		Guid revision = Guid.NewGuid();
		Guid category = Guid.NewGuid();
		DateTimeOffset cutoff = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
		Application.Models.Reports.CategoryReportBucket bucket = new(revision, category,
			Application.Models.Reports.CategoryReportState.Classified,
			[new Domain.Taxonomy.TaxonomyNode(category, null, "Archived food", null, true)], "USD", 12.3456m, 2);
		SpendingByCategoryResult expected = new([new("Archived food", bucket.Amount, 100) { Bucket = bucket }])
		{
			Snapshot = new(Application.Models.Reports.CategoryReportView.CurrentHierarchy, revision, [bucket],
			Application.Models.Reports.CategoryReportRollup.Root, cutoff,
			Application.Models.Reports.CategoryReportAssignmentSelection.LatestRecordedAtOrBeforeCutoff)
		};
		_mediatorMock.Setup(m => m.Send(It.Is<GetSpendingByCategoryQuery>(q =>
			q.CategoryOptions!.View == Application.Models.Reports.CategoryReportView.CurrentHierarchy
			&& q.CategoryOptions.CategoryId == category && q.CategoryOptions.CapturedCurrentRevisionId == revision
			&& q.CategoryOptions.KnowledgeCutoff == cutoff), CancellationToken.None)).ReturnsAsync(expected);
		var result = await _controller.GetSpendingByCategory(null, null, categoryView: "currentHierarchy", rollup: "root",
			categoryId: category, knowledgeCutoff: "2026-10-01T00:00:00Z", capturedCurrentRevisionId: revision);
		SpendingByCategoryResponse actual = Assert.IsType<Ok<SpendingByCategoryResponse>>(result.Result).Value!;
		actual.View.Should().Be("currentHierarchy");
		actual.CurrentRevisionId.Should().Be(revision);
		actual.KnowledgeCutoff.Should().Be(cutoff);
		actual.AssignmentSelection.Should().Be("latestRecordedAtOrBeforeCutoff");
		actual.Items.Single().Amount.Should().Be(12.3456);
		actual.Items.Single().Currency.Should().Be("USD");
		actual.Items.Single().CategoryId.Should().Be(category);
		actual.Items.Single().PathRevisionId.Should().Be(revision);
		actual.Items.Single().Path.Single().Archived.Should().BeTrue();
		typeof(DashboardController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Should().ContainSingle();
	}
}
