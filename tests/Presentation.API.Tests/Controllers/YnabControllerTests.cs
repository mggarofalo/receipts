using API.Controllers;
using API.Generated.Dtos;
using API.Mapping.Core;
using Application.Commands.Ynab.CategoryMapping;
using Application.Commands.Ynab.MemoSync;
using Application.Commands.Ynab.PushTransactions;
using Application.Commands.Ynab.SelectBudget;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Infrastructure.Ynab;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace Presentation.API.Tests.Controllers;

public class YnabControllerTests
{
	private readonly Mock<IMediator> _mediatorMock;
	private readonly Mock<IYnabApiClient> _ynabClientMock;
	private readonly Mock<IYnabBudgetSelectionService> _budgetSelectionMock;
	private readonly YnabMapper _mapper;
	private readonly Mock<ILogger<YnabController>> _loggerMock;
	private readonly YnabController _controller;

	public YnabControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_ynabClientMock = new Mock<IYnabApiClient>();
		_budgetSelectionMock = new Mock<IYnabBudgetSelectionService>();
		_mapper = new YnabMapper();
		_loggerMock = ControllerTestHelpers.GetLoggerMock<YnabController>();
		_controller = new YnabController(_mediatorMock.Object, _ynabClientMock.Object, _budgetSelectionMock.Object, _mapper, _loggerMock.Object)
		{
			ControllerContext = new ControllerContext
			{
				HttpContext = new DefaultHttpContext()
			}
		};
	}

	[Fact]
	public async Task GetBudgets_Returns200_WithBudgetList_WhenConfigured()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);

		List<YnabBudget> budgets =
		[
			new("budget-1", "My Budget"),
			new("budget-2", "Other Budget"),
		];

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetYnabBudgetsQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(budgets);

		// Act
		Results<Ok<YnabBudgetListResponse>, StatusCodeHttpResult> result = await _controller.GetBudgets(CancellationToken.None);

		// Assert
		Ok<YnabBudgetListResponse> okResult = Assert.IsType<Ok<YnabBudgetListResponse>>(result.Result);
		YnabBudgetListResponse response = okResult.Value!;
		response.Data.Should().HaveCount(2);
	}

	[Fact]
	public async Task GetBudgets_Returns503_WhenNotConfigured()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(false);

		// Act
		Results<Ok<YnabBudgetListResponse>, StatusCodeHttpResult> result = await _controller.GetBudgets(CancellationToken.None);

		// Assert
		StatusCodeHttpResult statusResult = Assert.IsType<StatusCodeHttpResult>(result.Result);
		statusResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
	}

	[Fact]
	public async Task GetBudgets_Returns503_OnYnabAuthException()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetYnabBudgetsQuery>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new YnabAuthException("Invalid token"));

		// Act
		Results<Ok<YnabBudgetListResponse>, StatusCodeHttpResult> result = await _controller.GetBudgets(CancellationToken.None);

		// Assert
		StatusCodeHttpResult statusResult = Assert.IsType<StatusCodeHttpResult>(result.Result);
		statusResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
	}

	[Fact]
	public async Task GetBudgetSettings_Returns200_WithSelection()
	{
		// Arrange
		string budgetId = Guid.NewGuid().ToString();
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetSelectedYnabBudgetQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabBudgetSelection(budgetId));

		// Act
		Ok<YnabBudgetSettingsResponse> result = await _controller.GetBudgetSettings(CancellationToken.None);

		// Assert
		YnabBudgetSettingsResponse response = result.Value!;
		response.SelectedBudgetId.Should().Be(budgetId);
	}

	[Fact]
	public async Task SelectBudget_Returns204()
	{
		// Arrange
		string budgetId = Guid.NewGuid().ToString();
		_mediatorMock.Setup(m => m.Send(
			It.Is<SelectYnabBudgetCommand>(c => c.BudgetId == budgetId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(Unit.Value);

		SelectYnabBudgetRequest request = new() { BudgetId = budgetId };

		// Act
		NoContent result = await _controller.SelectBudget(request, CancellationToken.None);

		// Assert
		Assert.IsType<NoContent>(result);
		_mediatorMock.Verify(m => m.Send(
			It.Is<SelectYnabBudgetCommand>(c => c.BudgetId == budgetId),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetCategories_Returns200_WithCategories_WhenConfigured()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync("budget-1");

		List<YnabCategory> categories =
		[
			new("cat-1", "Groceries", "group-1", "Needs", false),
			new("cat-2", "Rent", "group-1", "Needs", false),
			new("cat-3", "Hidden", "group-2", "Other", true),
		];

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetYnabCategoriesQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(categories);

		// Act
		Results<Ok<YnabCategoryListResponse>, StatusCodeHttpResult> result = await _controller.GetCategories(CancellationToken.None);

		// Assert
		Ok<YnabCategoryListResponse> okResult = Assert.IsType<Ok<YnabCategoryListResponse>>(result.Result);
		YnabCategoryListResponse response = okResult.Value!;
		response.Data.Should().HaveCount(2); // hidden excluded
	}

	[Fact]
	public async Task GetCategories_Returns503_WhenNotConfigured()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(false);

		// Act
		Results<Ok<YnabCategoryListResponse>, StatusCodeHttpResult> result = await _controller.GetCategories(CancellationToken.None);

		// Assert
		StatusCodeHttpResult statusResult = Assert.IsType<StatusCodeHttpResult>(result.Result);
		statusResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
	}

	[Fact]
	public async Task GetCategories_Returns503_WhenNoBudgetSelected()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		// Act
		Results<Ok<YnabCategoryListResponse>, StatusCodeHttpResult> result = await _controller.GetCategories(CancellationToken.None);

		// Assert
		StatusCodeHttpResult statusResult = Assert.IsType<StatusCodeHttpResult>(result.Result);
		statusResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
	}

	[Fact]
	public async Task GetCategoryMappings_Returns200_WithMappings()
	{
		// Arrange
		List<YnabCategoryMappingDto> mappings =
		[
			new(Guid.NewGuid(), "Groceries", "cat-1", "Groceries", "Needs", "budget-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
		];

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetAllYnabCategoryMappingsQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(mappings);

		// Act
		Ok<YnabCategoryMappingListResponse> result = await _controller.GetCategoryMappings(CancellationToken.None);

		// Assert
		YnabCategoryMappingListResponse response = result.Value!;
		response.Data.Should().HaveCount(1);
		response.Data.First().ReceiptsCategory.Should().Be("Groceries");
	}

	[Fact]
	public async Task CreateCategoryMapping_Returns201_OnSuccess()
	{
		// Arrange
		YnabCategoryMappingDto dto = new(
			Guid.NewGuid(),
			"Groceries",
			"cat-1",
			"Groceries",
			"Needs",
			"budget-1",
			DateTimeOffset.UtcNow,
			DateTimeOffset.UtcNow);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<CreateYnabCategoryMappingCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(dto);

		CreateYnabCategoryMappingRequest request = new()
		{
			ReceiptsCategory = "Groceries",
			YnabCategoryId = "cat-1",
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "Needs",
			YnabBudgetId = "budget-1",
		};

		// Act
		Results<Created<YnabCategoryMappingResponse>, Conflict<string>> result = await _controller.CreateCategoryMapping(request, CancellationToken.None);

		// Assert
		Created<YnabCategoryMappingResponse> createdResult = Assert.IsType<Created<YnabCategoryMappingResponse>>(result.Result);
		createdResult.Value!.ReceiptsCategory.Should().Be("Groceries");
	}

	[Fact]
	public async Task CreateCategoryMapping_Returns409_WhenDuplicate()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<CreateYnabCategoryMappingCommand>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("A mapping for receipts category 'Groceries' already exists."));

		CreateYnabCategoryMappingRequest request = new()
		{
			ReceiptsCategory = "Groceries",
			YnabCategoryId = "cat-1",
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "Needs",
			YnabBudgetId = "budget-1",
		};

		// Act
		Results<Created<YnabCategoryMappingResponse>, Conflict<string>> result = await _controller.CreateCategoryMapping(request, CancellationToken.None);

		// Assert
		Conflict<string> conflictResult = Assert.IsType<Conflict<string>>(result.Result);
		conflictResult.Value.Should().Contain("already exists");
	}

	[Fact]
	public async Task UpdateCategoryMapping_Returns204()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<UpdateYnabCategoryMappingCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(Unit.Value);

		UpdateYnabCategoryMappingRequest request = new()
		{
			YnabCategoryId = "cat-1",
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "Needs",
			YnabBudgetId = "budget-1",
		};

		// Act
		NoContent result = await _controller.UpdateCategoryMapping(id, request, CancellationToken.None);

		// Assert
		Assert.IsType<NoContent>(result);
	}

	[Fact]
	public async Task DeleteCategoryMapping_Returns204()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<DeleteYnabCategoryMappingCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(Unit.Value);

		// Act
		NoContent result = await _controller.DeleteCategoryMapping(id, CancellationToken.None);

		// Assert
		Assert.IsType<NoContent>(result);
	}

	[Fact]
	public async Task GetUnmappedCategories_Returns200_WithUnmapped()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetUnmappedCategoriesQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(["Electronics", "Pharmacy"]);

		// Act
		Ok<UnmappedCategoriesResponse> result = await _controller.GetUnmappedCategories(CancellationToken.None);

		// Assert
		UnmappedCategoriesResponse response = result.Value!;
		response.UnmappedCategories.Should().HaveCount(2);
		response.UnmappedCategories.Should().Contain("Electronics");
		response.UnmappedCategories.Should().Contain("Pharmacy");
	}

	[Fact]
	public async Task SyncMemos_Returns200_WithResults_WhenConfigured()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);

		Guid receiptId = Guid.NewGuid();
		Guid txId = Guid.NewGuid();
		List<Application.Models.Ynab.YnabMemoSyncResult> results =
		[
			new(txId, receiptId, Application.Models.Ynab.YnabMemoSyncOutcome.Synced, "yt-1", null, null),
		];

		_mediatorMock.Setup(m => m.Send(
			It.Is<SyncYnabMemosCommand>(c => c.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(results);

		SyncYnabMemosRequest request = new() { ReceiptId = receiptId };

		// Act
		Results<Ok<YnabMemoSyncResponse>, StatusCodeHttpResult> result = await _controller.SyncMemos(request, CancellationToken.None);

		// Assert
		Ok<YnabMemoSyncResponse> okResult = Assert.IsType<Ok<YnabMemoSyncResponse>>(result.Result);
		YnabMemoSyncResponse response = okResult.Value!;
		response.Results.Should().HaveCount(1);
		response.Results.First().Outcome.Should().Be(global::API.Generated.Dtos.YnabMemoSyncOutcome.Synced);
	}

	[Fact]
	public async Task SyncMemos_Returns503_WhenNotConfigured()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(false);

		SyncYnabMemosRequest request = new() { ReceiptId = Guid.NewGuid() };

		// Act
		Results<Ok<YnabMemoSyncResponse>, StatusCodeHttpResult> result = await _controller.SyncMemos(request, CancellationToken.None);

		// Assert
		StatusCodeHttpResult statusResult = Assert.IsType<StatusCodeHttpResult>(result.Result);
		statusResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
	}

	[Fact]
	public async Task SyncMemos_Returns503_OnYnabAuthException()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<SyncYnabMemosCommand>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new YnabAuthException("Invalid token"));

		SyncYnabMemosRequest request = new() { ReceiptId = Guid.NewGuid() };

		// Act
		Results<Ok<YnabMemoSyncResponse>, StatusCodeHttpResult> result = await _controller.SyncMemos(request, CancellationToken.None);

		// Assert
		StatusCodeHttpResult statusResult = Assert.IsType<StatusCodeHttpResult>(result.Result);
		statusResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
	}

	[Fact]
	public async Task SyncMemosBulk_Returns200_WithResults_WhenConfigured()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);

		Guid receiptId = Guid.NewGuid();
		List<Application.Models.Ynab.YnabMemoSyncResult> results =
		[
			new(Guid.NewGuid(), receiptId, Application.Models.Ynab.YnabMemoSyncOutcome.NoMatch, null, null, null),
		];

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<SyncYnabMemosBulkCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(results);

		SyncYnabMemosBulkRequest request = new() { ReceiptIds = [receiptId] };

		// Act
		Results<Ok<YnabMemoSyncResponse>, StatusCodeHttpResult> result = await _controller.SyncMemosBulk(request, CancellationToken.None);

		// Assert
		Ok<YnabMemoSyncResponse> okResult = Assert.IsType<Ok<YnabMemoSyncResponse>>(result.Result);
		okResult.Value!.Results.Should().HaveCount(1);
	}

	[Fact]
	public async Task SyncMemosBulk_Returns503_WhenNotConfigured()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(false);

		SyncYnabMemosBulkRequest request = new() { ReceiptIds = [Guid.NewGuid()] };

		// Act
		Results<Ok<YnabMemoSyncResponse>, StatusCodeHttpResult> result = await _controller.SyncMemosBulk(request, CancellationToken.None);

		// Assert
		StatusCodeHttpResult statusResult = Assert.IsType<StatusCodeHttpResult>(result.Result);
		statusResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
	}

	[Fact]
	public async Task ResolveMemoSync_Returns200_WithResult_WhenConfigured()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);

		Guid txId = Guid.NewGuid();
		Application.Models.Ynab.YnabMemoSyncResult expected = new(
			txId, Guid.NewGuid(), Application.Models.Ynab.YnabMemoSyncOutcome.Synced, "yt-1", null, null);

		_mediatorMock.Setup(m => m.Send(
			It.Is<ResolveYnabMemoSyncCommand>(c => c.LocalTransactionId == txId && c.YnabTransactionId == "yt-1"),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		ResolveYnabMemoSyncRequest request = new() { LocalTransactionId = txId, YnabTransactionId = "yt-1" };

		// Act
		Results<Ok<YnabMemoSyncResultItem>, StatusCodeHttpResult> result = await _controller.ResolveMemoSync(request, CancellationToken.None);

		// Assert
		Ok<YnabMemoSyncResultItem> okResult = Assert.IsType<Ok<YnabMemoSyncResultItem>>(result.Result);
		okResult.Value!.Outcome.Should().Be(global::API.Generated.Dtos.YnabMemoSyncOutcome.Synced);
	}

	[Fact]
	public async Task ResolveMemoSync_Returns503_WhenNotConfigured()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(false);

		ResolveYnabMemoSyncRequest request = new() { LocalTransactionId = Guid.NewGuid(), YnabTransactionId = "yt-1" };

		// Act
		Results<Ok<YnabMemoSyncResultItem>, StatusCodeHttpResult> result = await _controller.ResolveMemoSync(request, CancellationToken.None);

		// Assert
		StatusCodeHttpResult statusResult = Assert.IsType<StatusCodeHttpResult>(result.Result);
		statusResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
	}

	[Fact]
	public async Task PushTransactions_Returns200_OnSuccess()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);
		Guid receiptId = Guid.NewGuid();
		Guid txId = Guid.NewGuid();

		PushYnabTransactionsResult pushResult = new(true,
		[
			new Application.Commands.Ynab.PushTransactions.PushedTransactionInfo(txId, "ynab-tx-1", -15000, 2),
		]);

		_mediatorMock.Setup(m => m.Send(
			It.Is<PushYnabTransactionsCommand>(c => c.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(pushResult);

		PushYnabTransactionsRequest request = new() { ReceiptId = receiptId };

		// Act
		Results<Ok<PushYnabTransactionsResponse>, BadRequest<PushYnabTransactionsResponse>, StatusCodeHttpResult> result =
			await _controller.PushTransactions(request, CancellationToken.None);

		// Assert
		Ok<PushYnabTransactionsResponse> okResult = Assert.IsType<Ok<PushYnabTransactionsResponse>>(result.Result);
		PushYnabTransactionsResponse response = okResult.Value!;
		response.Success.Should().BeTrue();
		response.PushedTransactions.Should().HaveCount(1);
		response.PushedTransactions.First().YnabTransactionId.Should().Be("ynab-tx-1");
	}

	[Fact]
	public async Task PushTransactions_Returns400_OnFailure()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);
		Guid receiptId = Guid.NewGuid();

		PushYnabTransactionsResult pushResult = new(false, [],
			UnmappedCategories: ["Electronics"], Error: "Unmapped categories found.");

		_mediatorMock.Setup(m => m.Send(
			It.Is<PushYnabTransactionsCommand>(c => c.ReceiptId == receiptId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(pushResult);

		PushYnabTransactionsRequest request = new() { ReceiptId = receiptId };

		// Act
		Results<Ok<PushYnabTransactionsResponse>, BadRequest<PushYnabTransactionsResponse>, StatusCodeHttpResult> result =
			await _controller.PushTransactions(request, CancellationToken.None);

		// Assert
		BadRequest<PushYnabTransactionsResponse> badResult = Assert.IsType<BadRequest<PushYnabTransactionsResponse>>(result.Result);
		PushYnabTransactionsResponse response = badResult.Value!;
		response.Success.Should().BeFalse();
		response.UnmappedCategories.Should().Contain("Electronics");
	}

	[Fact]
	public async Task PushTransactions_Returns503_WhenNotConfigured()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(false);
		PushYnabTransactionsRequest request = new() { ReceiptId = Guid.NewGuid() };

		// Act
		Results<Ok<PushYnabTransactionsResponse>, BadRequest<PushYnabTransactionsResponse>, StatusCodeHttpResult> result =
			await _controller.PushTransactions(request, CancellationToken.None);

		// Assert
		StatusCodeHttpResult statusResult = Assert.IsType<StatusCodeHttpResult>(result.Result);
		statusResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
	}

	[Fact]
	public async Task BulkPushTransactions_Returns200_WithResults()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);
		Guid receiptId1 = Guid.NewGuid();
		Guid receiptId2 = Guid.NewGuid();

		BulkPushYnabTransactionsResult bulkResult = new([
			new Application.Commands.Ynab.PushTransactions.ReceiptPushResult(receiptId1, new PushYnabTransactionsResult(true, [new Application.Commands.Ynab.PushTransactions.PushedTransactionInfo(Guid.NewGuid(), "ynab-tx-1", -10000, 1)])),
			new Application.Commands.Ynab.PushTransactions.ReceiptPushResult(receiptId2, new PushYnabTransactionsResult(false, [], Error: "Receipt not found.")),
		]);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<BulkPushYnabTransactionsCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(bulkResult);

		BulkPushYnabTransactionsRequest request = new() { ReceiptIds = [receiptId1, receiptId2] };

		// Act
		Results<Ok<BulkPushYnabTransactionsResponse>, StatusCodeHttpResult> result =
			await _controller.BulkPushTransactions(request, CancellationToken.None);

		// Assert
		Ok<BulkPushYnabTransactionsResponse> okResult = Assert.IsType<Ok<BulkPushYnabTransactionsResponse>>(result.Result);
		BulkPushYnabTransactionsResponse response = okResult.Value!;
		response.Results.Should().HaveCount(2);
	}
}
