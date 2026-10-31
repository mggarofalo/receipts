using API.Controllers.Aggregates;
using API.Generated.Dtos;
using API.Http;
using Application.Queries.Aggregates.Reports;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using AppReports = Application.Models.Reports;

namespace Presentation.API.Tests.Controllers.Aggregates;

public class UncategorizedItemsReportsControllerTests
{
	private readonly Mock<IMediator> _mediatorMock;
	private readonly ReportsController _controller;

	public UncategorizedItemsReportsControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_controller = new ReportsController(_mediatorMock.Object, Mock.Of<global::API.Services.IEntityChangeNotifier>());
	}

	[Fact]
	public async Task GetUncategorizedItems_ReturnsOkResult_WithDefaultParameters()
	{
		// Arrange
		AppReports.UncategorizedItemsResult reportResult = new(
		[
			new AppReports.UncategorizedItemRecord(
				Guid.NewGuid(), Guid.NewGuid(), "ABC",
				"Test Item", 1m, 5.00m, 5.00m, "Uncategorized", null),
		], 1);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetUncategorizedItemsReportQuery>(q =>
				q.SortBy == "description" && q.SortDirection == "asc" && q.Page == 1 && q.PageSize == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<UncategorizedItemsResponse>, BadRequestProblem> result =
			await _controller.GetUncategorizedItems(null, null, null, null, null, CancellationToken.None);

		// Assert
		Ok<UncategorizedItemsResponse> okResult = Assert.IsType<Ok<UncategorizedItemsResponse>>(result.Result);
		UncategorizedItemsResponse response = okResult.Value!;
		response.TotalCount.Should().Be(1);
		response.Items.Should().ContainSingle();
		response.Items.First().Description.Should().Be("Test Item");
	}

	[Fact]
	public async Task GetUncategorizedItems_PassesCustomParameters()
	{
		// Arrange
		AppReports.UncategorizedItemsResult reportResult = new([], 0);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetUncategorizedItemsReportQuery>(q =>
				q.SortBy == "total" && q.SortDirection == "desc" && q.Page == 2 && q.PageSize == 25),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<UncategorizedItemsResponse>, BadRequestProblem> result =
			await _controller.GetUncategorizedItems("total", "desc", 2, 25, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<UncategorizedItemsResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetUncategorizedItemsReportQuery>(q =>
				q.SortBy == "total" && q.SortDirection == "desc" && q.Page == 2 && q.PageSize == 25),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetUncategorizedItems_ReturnsBadRequest_WhenInvalidSortBy()
	{
		// Act
		Results<Ok<UncategorizedItemsResponse>, BadRequestProblem> result =
			await _controller.GetUncategorizedItems("invalid", null, null, null, null, CancellationToken.None);

		// Assert
		BadRequestProblem badResult = Assert.IsType<BadRequestProblem>(result.Result);
		badResult.Value!.Detail.Should().Contain("Invalid sortBy");
	}

	[Fact]
	public async Task GetUncategorizedItems_ReturnsBadRequest_WhenInvalidSortDirection()
	{
		// Act
		Results<Ok<UncategorizedItemsResponse>, BadRequestProblem> result =
			await _controller.GetUncategorizedItems(null, "invalid", null, null, null, CancellationToken.None);

		// Assert
		BadRequestProblem badResult = Assert.IsType<BadRequestProblem>(result.Result);
		badResult.Value!.Detail.Should().Contain("Invalid sortDirection");
	}

	[Fact]
	public async Task GetUncategorizedItems_ReturnsBadRequest_WhenPageLessThanOne()
	{
		// Act
		Results<Ok<UncategorizedItemsResponse>, BadRequestProblem> result =
			await _controller.GetUncategorizedItems(null, null, 0, null, null, CancellationToken.None);

		// Assert
		BadRequestProblem badResult = Assert.IsType<BadRequestProblem>(result.Result);
		badResult.Value!.Detail.Should().Be("page must be at least 1");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(101)]
	public async Task GetUncategorizedItems_ReturnsBadRequest_WhenPageSizeOutOfRange(int pageSize)
	{
		// Act
		Results<Ok<UncategorizedItemsResponse>, BadRequestProblem> result =
			await _controller.GetUncategorizedItems(null, null, null, pageSize, null, CancellationToken.None);

		// Assert
		BadRequestProblem badResult = Assert.IsType<BadRequestProblem>(result.Result);
		badResult.Value!.Detail.Should().Be("pageSize must be between 1 and 100");
	}

	[Theory]
	[InlineData("description")]
	[InlineData("total")]
	[InlineData("itemCode")]
	public async Task GetUncategorizedItems_AcceptsValidSortColumns(string sortBy)
	{
		// Arrange
		AppReports.UncategorizedItemsResult reportResult = new([], 0);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetUncategorizedItemsReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<UncategorizedItemsResponse>, BadRequestProblem> result =
			await _controller.GetUncategorizedItems(sortBy, null, null, null, null, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<UncategorizedItemsResponse>>(result.Result);
	}

	[Fact]
	public async Task GetUncategorizedItems_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetUncategorizedItemsReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetUncategorizedItems(null, null, null, null, null, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetUncategorizedItems_MapsResponseFieldsCorrectly()
	{
		// Arrange
		Guid itemId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();

		AppReports.UncategorizedItemsResult reportResult = new(
		[
			new AppReports.UncategorizedItemRecord(
				itemId, receiptId, "ITM-001",
				"Test Description", 2m, 3.50m, 7.00m, "Uncategorized", "SomeSub"),
		], 1);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetUncategorizedItemsReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<UncategorizedItemsResponse>, BadRequestProblem> result =
			await _controller.GetUncategorizedItems(null, null, null, null, null, CancellationToken.None);

		// Assert
		Ok<UncategorizedItemsResponse> okResult = Assert.IsType<Ok<UncategorizedItemsResponse>>(result.Result);
		UncategorizedItem item = okResult.Value!.Items.First();
		item.Id.Should().Be(itemId);
		item.ReceiptId.Should().Be(receiptId);
		item.ReceiptItemCode.Should().Be("ITM-001");
		item.Description.Should().Be("Test Description");
		item.Quantity.Should().Be(2.0);
		item.UnitPrice.Should().Be(3.50);
		item.TotalAmount.Should().Be(7.00);
		item.Category.Should().Be("Uncategorized");
		item.Subcategory.Should().Be("SomeSub");
	}

	[Fact]
	public async Task GetUncategorizedItems_MapsLocationAndSuggestion()
	{
		// Arrange
		Guid attemptId = Guid.NewGuid(), root = Guid.NewGuid(), leaf = Guid.NewGuid();
		AppReports.UncategorizedItemsResult reportResult = new(
		[
			new AppReports.UncategorizedItemRecord(
				Guid.NewGuid(), Guid.NewGuid(), null, "With answer", 1m, 1m, 1m, "Uncategorized", null)
			{
				Location = "Corner Market",
				Suggestion = new AppReports.UncategorizedItemSuggestion(attemptId, "stale", leaf, 0.82, null,
					[new Application.Classification.CategoryAssistanceCandidateRead(root, 0.9)],
					[new Application.Classification.CategoryAssistanceCandidateRead(leaf, 0.8)]),
			},
			new AppReports.UncategorizedItemRecord(
				Guid.NewGuid(), Guid.NewGuid(), null, "Without answer", 1m, 1m, 1m, "Uncategorized", null)
			{
				Location = "Other Store",
			},
		], 2);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetUncategorizedItemsReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(reportResult);

		// Act
		Results<Ok<UncategorizedItemsResponse>, BadRequestProblem> result =
			await _controller.GetUncategorizedItems(null, null, null, null, null, CancellationToken.None);

		// Assert
		Ok<UncategorizedItemsResponse> okResult = Assert.IsType<Ok<UncategorizedItemsResponse>>(result.Result);
		List<UncategorizedItem> items = [.. okResult.Value!.Items];
		items[0].Location.Should().Be("Corner Market");
		UncategorizedItemSuggestion suggestion = items[0].Suggestion!;
		suggestion.AttemptId.Should().Be(attemptId);
		suggestion.State.Should().Be("stale");
		suggestion.CategoryId.Should().Be(leaf);
		suggestion.Confidence.Should().Be(0.82);
		suggestion.AbstentionReason.Should().BeNull();
		suggestion.RootCandidates.Should().ContainSingle()
			.Which.Should().BeEquivalentTo(new { CategoryId = root, Probability = 0.9 });
		suggestion.LeafCandidates.Should().ContainSingle()
			.Which.Should().BeEquivalentTo(new { CategoryId = leaf, Probability = 0.8 });
		items[1].Location.Should().Be("Other Store");
		items[1].Suggestion.Should().BeNull();
	}

	[Theory]
	[InlineData(null, "any")]
	[InlineData("with", "with")]
	[InlineData("without", "without")]
	[InlineData("With", "With")]
	public async Task GetUncategorizedItems_PassesSuggestionFilter(string? suggestion, string expected)
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetUncategorizedItemsReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new AppReports.UncategorizedItemsResult([], 0));

		// Act
		Results<Ok<UncategorizedItemsResponse>, BadRequestProblem> result =
			await _controller.GetUncategorizedItems(null, null, null, null, suggestion, CancellationToken.None);

		// Assert
		Assert.IsType<Ok<UncategorizedItemsResponse>>(result.Result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<GetUncategorizedItemsReportQuery>(q => q.SuggestionFilter == expected),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetUncategorizedItems_ReturnsBadRequest_WhenInvalidSuggestion()
	{
		// Act
		Results<Ok<UncategorizedItemsResponse>, BadRequestProblem> result =
			await _controller.GetUncategorizedItems(null, null, null, null, "maybe", CancellationToken.None);

		// Assert
		BadRequestProblem badResult = Assert.IsType<BadRequestProblem>(result.Result);
		badResult.Value!.Detail.Should().Be("Invalid suggestion 'maybe'. Allowed: any, with, without");
		_mediatorMock.Verify(m => m.Send(
			It.IsAny<GetUncategorizedItemsReportQuery>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task GetUncategorizedItems_MapsSuggestionsAvailable(bool available)
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetUncategorizedItemsReportQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new AppReports.UncategorizedItemsResult([], 0) { SuggestionsAvailable = available });

		// Act
		Results<Ok<UncategorizedItemsResponse>, BadRequestProblem> result =
			await _controller.GetUncategorizedItems(null, null, null, null, null, CancellationToken.None);

		// Assert
		Ok<UncategorizedItemsResponse> okResult = Assert.IsType<Ok<UncategorizedItemsResponse>>(result.Result);
		okResult.Value!.SuggestionsAvailable.Should().Be(available);
	}
}
