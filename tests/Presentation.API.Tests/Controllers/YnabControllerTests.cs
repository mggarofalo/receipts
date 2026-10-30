using API.Controllers;
using API.Generated.Dtos;
using API.Http;
using API.Mapping.Core;
using Application.Commands.Ynab.EnvelopeMapping;
using Application.Commands.Ynab.MemoSync;
using Application.Commands.Ynab.PushTransactions;
using Application.Commands.Ynab.SelectBudget;
using Application.Commands.Ynab.StaleMappings;
using Application.Exceptions;
using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using Common;
using FluentAssertions;
using Infrastructure.Ynab;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace Presentation.API.Tests.Controllers;

public class YnabControllerTests
{
	[Theory]
	[InlineData(null, YnabComparisonCategoryView.AsClassified)]
	[InlineData("asClassified", YnabComparisonCategoryView.AsClassified)]
	[InlineData("currentHierarchy", YnabComparisonCategoryView.CurrentHierarchy)]
	public async Task GetReceiptSplitComparison_UsesExplicitCategoryView(
		string? categoryView, YnabComparisonCategoryView expectedView)
	{
		Guid receiptId = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
				It.Is<GetReceiptYnabSplitComparisonQuery>(q => q.ReceiptId == receiptId && q.CategoryView == expectedView),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ReceiptYnabSplitComparisonResult(false, "Unavailable", [], [])
			{
				CategoryView = expectedView == YnabComparisonCategoryView.CurrentHierarchy
					? "currentHierarchy" : "asClassified",
			});

		var result = await _controller.GetReceiptSplitComparison(receiptId, categoryView);

		var response = result.Result.Should().BeOfType<Ok<ReceiptYnabSplitComparisonResponse>>().Subject.Value!;
		response.CategoryView.Should().Be(expectedView == YnabComparisonCategoryView.CurrentHierarchy
			? ReceiptYnabSplitComparisonResponseCategoryView.CurrentHierarchy
			: ReceiptYnabSplitComparisonResponseCategoryView.AsClassified);
	}

	[Fact]
	public async Task GetReceiptSplitComparison_RejectsUnknownCategoryView()
	{
		var result = await _controller.GetReceiptSplitComparison(Guid.NewGuid(), "unknown");
		result.Result.Should().BeOfType<BadRequestProblem>();
		_mediatorMock.Verify(m => m.Send(It.IsAny<GetReceiptYnabSplitComparisonQuery>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}
	private static readonly Guid Budget1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid BudgetB = Guid.Parse("22222222-2222-2222-2222-222222222222");
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
		_controller = new YnabController(_mediatorMock.Object, _ynabClientMock.Object, _budgetSelectionMock.Object, _mapper, new EnvelopeMapper(), _loggerMock.Object)
		{
			ControllerContext = new ControllerContext
			{
				HttpContext = new DefaultHttpContext()
			}
		};
	}

	[Fact]
	public async Task GetConnectionStatus_ReturnsConnected_WhenConfiguredAndReachable()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetYnabConnectionStatusQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabConnectionStatus(true, true, DateTimeOffset.UtcNow));

		// Act
		Ok<YnabConnectionStatusResponse> result = await _controller.GetConnectionStatus(CancellationToken.None);

		// Assert
		YnabConnectionStatusResponse response = result.Value!;
		response.IsConfigured.Should().BeTrue();
		response.IsConnected.Should().BeTrue();
		response.LastSuccessfulSyncUtc.Should().NotBeNull();
	}

	[Fact]
	public async Task GetStatus_ReturnsMappedStats()
	{
		_mediatorMock.Setup(m => m.Send(It.IsAny<GetYnabStatusQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabStatus(true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, 1, 2, 3, 3, 0));

		Ok<YnabStatusResponse> result = await _controller.GetStatus(CancellationToken.None);

		result.Value!.IsConfigured.Should().BeTrue();
		result.Value.PushCountLast30d.Should().Be(3);
		result.Value.PushSuccessLast30d.Should().Be(3);
	}

	[Fact]
	public async Task GetEvents_ReturnsMappedList()
	{
		PagedResult<YnabSyncEventDto> paged = new(
			[new YnabSyncEventDto(Guid.NewGuid(), DateTimeOffset.UtcNow, "Push", null, null, 201, true, null, null)],
			1, 0, 50);
		_mediatorMock.Setup(m => m.Send(It.IsAny<GetYnabSyncEventsQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(paged);

		Results<Ok<YnabSyncEventListResponse>, BadRequestProblem> result =
			await _controller.GetEvents(cancellationToken: CancellationToken.None);

		Ok<YnabSyncEventListResponse> ok = result.Result.Should().BeOfType<Ok<YnabSyncEventListResponse>>().Subject;
		ok.Value!.Total.Should().Be(1);
		ok.Value.Data.Should().HaveCount(1);
	}

	[Theory]
	[InlineData(-1, 50, null)]
	[InlineData(0, 0, null)]
	[InlineData(0, 501, null)]
	[InlineData(0, 50, "bogus")]
	public async Task GetEvents_ReturnsBadRequest_ForInvalidArguments(int offset, int limit, string? outcome)
	{
		Results<Ok<YnabSyncEventListResponse>, BadRequestProblem> result =
			await _controller.GetEvents(offset: offset, limit: limit, outcome: outcome, cancellationToken: CancellationToken.None);

		result.Result.Should().BeOfType<BadRequestProblem>();
	}

	[Fact]
	public async Task GetConnectionStatus_ReturnsNotConfigured_WhenPatMissing()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetYnabConnectionStatusQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabConnectionStatus(false, false, null));

		// Act
		Ok<YnabConnectionStatusResponse> result = await _controller.GetConnectionStatus(CancellationToken.None);

		// Assert
		YnabConnectionStatusResponse response = result.Value!;
		response.IsConfigured.Should().BeFalse();
		response.IsConnected.Should().BeFalse();
		response.LastSuccessfulSyncUtc.Should().BeNull();
	}

	[Fact]
	public async Task GetConnectionStatus_ReturnsDisconnected_WhenAuthFails()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetYnabConnectionStatusQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabConnectionStatus(true, false, null));

		// Act
		Ok<YnabConnectionStatusResponse> result = await _controller.GetConnectionStatus(CancellationToken.None);

		// Assert
		YnabConnectionStatusResponse response = result.Value!;
		response.IsConfigured.Should().BeTrue();
		response.IsConnected.Should().BeFalse();
	}

	[Fact]
	public async Task GetBudgets_Returns200_WithBudgetList_WhenConfigured()
	{
		// Arrange
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
	public async Task GetBudgets_Returns503_OnYnabAuthException()
	{
		// Arrange
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
		Guid requestBudgetId = Guid.NewGuid();
		string budgetId = requestBudgetId.ToString("D");
		_mediatorMock.Setup(m => m.Send(
			It.Is<SelectYnabBudgetCommand>(c => c.BudgetId == budgetId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(Unit.Value);

		SelectYnabBudgetRequest request = new() { BudgetId = requestBudgetId };

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
	public async Task GetCategories_Returns503_WhenNoBudgetSelected()
	{
		// Arrange
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		// Act
		Results<Ok<YnabCategoryListResponse>, StatusCodeHttpResult> result = await _controller.GetCategories(CancellationToken.None);

		// Assert
		StatusCodeHttpResult statusResult = Assert.IsType<StatusCodeHttpResult>(result.Result);
		statusResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
	}

	[Fact]
	public async Task GetEnvelopeMappings_Returns200_WithMappings()
	{
		// Arrange
		Guid envelopeId = Guid.NewGuid();
		List<YnabEnvelopeMappingDto> mappings =
		[
			new(Guid.NewGuid(), envelopeId, "Groceries", "cat-1", "Groceries", "Needs", "budget-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
		];
		_mediatorMock.Setup(m => m.Send(It.IsAny<GetYnabEnvelopeMappingsQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(mappings);

		// Act
		Ok<YnabEnvelopeMappingListResponse> result = await _controller.GetEnvelopeMappings(CancellationToken.None);

		// Assert
		YnabEnvelopeMappingResponse actual = result.Value!.Data.Should().ContainSingle().Subject;
		actual.EnvelopeId.Should().Be(envelopeId);
		actual.EnvelopeName.Should().Be("Groceries");
		actual.YnabCategoryId.Should().Be("cat-1");
	}

	[Fact]
	public async Task GetUnmappedEnvelopes_Returns200_WithEnvelopes()
	{
		// Arrange
		Guid envelopeId = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(It.IsAny<GetUnmappedEnvelopesQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync([new Domain.Envelopes.Envelope(envelopeId, "Gifts", false)]);

		// Act
		Ok<EnvelopeListResponse> result = await _controller.GetUnmappedEnvelopes(CancellationToken.None);

		// Assert
		EnvelopeResponse actual = result.Value!.Data.Should().ContainSingle().Subject;
		actual.Id.Should().Be(envelopeId);
		actual.Name.Should().Be("Gifts");
		actual.IsActive.Should().BeFalse();
	}

	[Fact]
	public async Task SetEnvelopeMapping_Returns200_AndSendsRouteEnvelopeWithCanonicalBudget()
	{
		// Arrange
		Guid envelopeId = Guid.NewGuid();
		YnabEnvelopeMappingDto dto = new(Guid.NewGuid(), envelopeId, "Groceries", "cat-1", "Groceries", "Needs",
			Budget1.ToString("D"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
		_mediatorMock.Setup(m => m.Send(
				new SetYnabEnvelopeMappingCommand(envelopeId, Budget1.ToString("D"), "cat-1", "Groceries", "Needs"),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(dto);
		SetYnabEnvelopeMappingRequest request = new()
		{
			YnabCategoryId = "cat-1",
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "Needs",
			YnabBudgetId = Budget1,
		};

		// Act
		Results<Ok<YnabEnvelopeMappingResponse>, BadRequestProblem, NotFoundProblem> result =
			await _controller.SetEnvelopeMapping(envelopeId, request, CancellationToken.None);

		// Assert
		Ok<YnabEnvelopeMappingResponse> ok = Assert.IsType<Ok<YnabEnvelopeMappingResponse>>(result.Result);
		ok.Value!.EnvelopeId.Should().Be(envelopeId);
		ok.Value.YnabBudgetId.Should().Be(Budget1.ToString("D"));
	}

	[Fact]
	public async Task SetEnvelopeMapping_Returns400_WhenRequestBudgetIsNotSelectedBudget()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(It.IsAny<SetYnabEnvelopeMappingCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new ArgumentException("The mapping budget must match the selected YNAB budget."));
		SetYnabEnvelopeMappingRequest request = new()
		{
			YnabCategoryId = "ynab-B-category",
			YnabCategoryName = "Budget B Groceries",
			YnabCategoryGroupName = "Needs",
			YnabBudgetId = BudgetB,
		};

		// Act
		Results<Ok<YnabEnvelopeMappingResponse>, BadRequestProblem, NotFoundProblem> result =
			await _controller.SetEnvelopeMapping(Guid.NewGuid(), request, CancellationToken.None);

		// Assert
		BadRequestProblem badRequest = Assert.IsType<BadRequestProblem>(result.Result);
		badRequest.Value!.Detail.Should().Contain("selected YNAB budget");
	}

	[Fact]
	public async Task SetEnvelopeMapping_Returns404Problem_WhenEnvelopeMissing()
	{
		// Arrange
		Guid envelopeId = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(It.IsAny<SetYnabEnvelopeMappingCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException());
		SetYnabEnvelopeMappingRequest request = new()
		{
			YnabCategoryId = "cat-1",
			YnabCategoryName = "Groceries",
			YnabCategoryGroupName = "Needs",
			YnabBudgetId = Budget1,
		};

		// Act
		Results<Ok<YnabEnvelopeMappingResponse>, BadRequestProblem, NotFoundProblem> result =
			await _controller.SetEnvelopeMapping(envelopeId, request, CancellationToken.None);

		// Assert
		NotFoundProblem notFound = Assert.IsType<NotFoundProblem>(result.Result);
		notFound.Value!.Detail.Should().Contain(envelopeId.ToString());
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task DeleteEnvelopeMapping_ReturnsNoContentOnlyWhenDeleted(bool deleted)
	{
		// Arrange
		Guid envelopeId = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(new DeleteYnabEnvelopeMappingCommand(envelopeId), It.IsAny<CancellationToken>()))
			.ReturnsAsync(deleted);

		// Act
		Results<NoContent, NotFound> result = await _controller.DeleteEnvelopeMapping(envelopeId, CancellationToken.None);

		// Assert
		if (deleted)
		{
			result.Result.Should().BeOfType<NoContent>();
		}
		else
		{
			result.Result.Should().BeOfType<NotFound>();
		}
	}

	[Fact]
	public async Task SyncMemos_Returns200_WithResults_WhenConfigured()
	{
		// Arrange
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
	public async Task SyncMemos_Returns503_OnYnabAuthException()
	{
		// Arrange
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
	public async Task ResolveMemoSync_Returns200_WithResult_WhenConfigured()
	{
		// Arrange
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
	public async Task PushTransactions_Returns200_OnSuccess()
	{
		// Arrange
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
		Guid receiptId = Guid.NewGuid();

		PushYnabTransactionsResult pushResult = new(false, [],
			UnmappedCategories: ["Electronics"], Error: "Unmapped envelopes found.",
			OperationStatus: YnabSyncStatus.Unknown);

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
		response.OperationStatus.Should().Be(YnabSyncRecordResponseSyncStatus.Unknown);
		response.UnmappedCategories.Should().Contain("Electronics");
	}

	[Fact]
	public async Task BulkPushTransactions_Returns200_WithResults()
	{
		// Arrange
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

	[Fact]
	public async Task GetStaleMappings_Returns200_WithStaleCounts()
	{
		// Arrange
		string budgetId = Guid.NewGuid().ToString();
		StaleMappingsResult staleMappings = new(3, 5, budgetId);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetStaleMappingsQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(staleMappings);

		// Act
		Ok<StaleMappingsResponse> result = await _controller.GetStaleMappings(CancellationToken.None);

		// Assert
		StaleMappingsResponse response = result.Value!;
		response.StaleAccountMappingCount.Should().Be(3);
		response.StaleCategoryMappingCount.Should().Be(5);
		response.CurrentBudgetId.Should().Be(budgetId);
	}

	[Fact]
	public async Task GetStaleMappings_Returns200_WithZeroCounts_WhenNoBudgetSelected()
	{
		// Arrange
		StaleMappingsResult staleMappings = new(0, 0, null);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetStaleMappingsQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(staleMappings);

		// Act
		Ok<StaleMappingsResponse> result = await _controller.GetStaleMappings(CancellationToken.None);

		// Assert
		StaleMappingsResponse response = result.Value!;
		response.StaleAccountMappingCount.Should().Be(0);
		response.StaleCategoryMappingCount.Should().Be(0);
		response.CurrentBudgetId.Should().BeNull();
	}

	[Fact]
	public async Task ClearStaleMappings_Returns200_WithDeletedCounts()
	{
		// Arrange
		ClearStaleMappingsResult clearResult = new(2, 4);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<ClearStaleMappingsCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(clearResult);

		// Act
		Ok<ClearStaleMappingsResponse> result = await _controller.ClearStaleMappings(CancellationToken.None);

		// Assert
		ClearStaleMappingsResponse response = result.Value!;
		response.DeletedAccountMappings.Should().Be(2);
		response.DeletedCategoryMappings.Should().Be(4);
	}

	[Fact]
	public async Task ClearStaleMappings_Returns200_WithZeros_WhenNothingToDelete()
	{
		// Arrange
		ClearStaleMappingsResult clearResult = new(0, 0);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<ClearStaleMappingsCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(clearResult);

		// Act
		Ok<ClearStaleMappingsResponse> result = await _controller.ClearStaleMappings(CancellationToken.None);

		// Assert
		ClearStaleMappingsResponse response = result.Value!;
		response.DeletedAccountMappings.Should().Be(0);
		response.DeletedCategoryMappings.Should().Be(0);
	}
}
