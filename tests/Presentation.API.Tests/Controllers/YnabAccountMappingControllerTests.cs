using API.Controllers;
using API.Generated.Dtos;
using API.Mapping.Core;
using Application.Commands.Ynab.AccountMapping;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace Presentation.API.Tests.Controllers;

public class YnabAccountMappingControllerTests
{
	private readonly Mock<IMediator> _mediatorMock;
	private readonly Mock<IYnabApiClient> _ynabClientMock;
	private readonly Mock<IYnabBudgetSelectionService> _budgetSelectionMock;
	private readonly YnabMapper _mapper;
	private readonly Mock<ILogger<YnabController>> _loggerMock;
	private readonly YnabController _controller;

	public YnabAccountMappingControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_ynabClientMock = new Mock<IYnabApiClient>();
		_budgetSelectionMock = new Mock<IYnabBudgetSelectionService>();
		_mapper = new YnabMapper();
		_loggerMock = ControllerTestHelpers.GetLoggerMock<YnabController>();
		_controller = new YnabController(_mediatorMock.Object, _ynabClientMock.Object, _budgetSelectionMock.Object, _mapper, _loggerMock.Object);
		_controller.ControllerContext = new ControllerContext
		{
			HttpContext = new DefaultHttpContext()
		};
	}

	[Fact]
	public async Task GetAccountMappings_Returns200_WithMappingList()
	{
		// Arrange
		List<YnabAccountMappingDto> mappings =
		[
			new(Guid.NewGuid(), Guid.NewGuid(), "ynab-1", "Checking", "budget-1",
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
		];

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetYnabAccountMappingsQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(mappings);

		// Act
		Ok<YnabAccountMappingListResponse> result = await _controller.GetAccountMappings(CancellationToken.None);

		// Assert
		YnabAccountMappingListResponse response = result.Value!;
		response.Data.Should().HaveCount(1);
	}

	[Fact]
	public async Task CreateAccountMapping_Returns201_WhenValid()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		YnabAccountMappingDto mapping = new(
			Guid.NewGuid(), accountId, "ynab-1", "Checking", "budget-1",
			DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<CreateYnabAccountMappingCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(mapping);

		CreateYnabAccountMappingRequest request = new()
		{
			ReceiptsAccountId = accountId,
			YnabAccountId = "ynab-1",
			YnabAccountName = "Checking",
			YnabBudgetId = "budget-1",
		};

		// Act
		Results<Created<YnabAccountMappingResponse>, BadRequest<string>> result =
			await _controller.CreateAccountMapping(request, CancellationToken.None);

		// Assert
		Created<YnabAccountMappingResponse> created = Assert.IsType<Created<YnabAccountMappingResponse>>(result.Result);
		created.Value!.YnabAccountId.Should().Be("ynab-1");
		created.Location.Should().Contain("/api/ynab/account-mappings/");
	}

	[Fact]
	public async Task CreateAccountMapping_Returns400_WhenAccountDoesNotExist()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.IsAny<CreateYnabAccountMappingCommand>(),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("Account does not exist."));

		CreateYnabAccountMappingRequest request = new()
		{
			ReceiptsAccountId = Guid.NewGuid(),
			YnabAccountId = "ynab-1",
			YnabAccountName = "Checking",
			YnabBudgetId = "budget-1",
		};

		// Act
		Results<Created<YnabAccountMappingResponse>, BadRequest<string>> result =
			await _controller.CreateAccountMapping(request, CancellationToken.None);

		// Assert
		BadRequest<string> badRequest = Assert.IsType<BadRequest<string>>(result.Result);
		badRequest.Value.Should().Be("Account does not exist.");
	}

	[Fact]
	public async Task UpdateAccountMapping_Returns204_WhenFound()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		YnabAccountMappingDto existing = new(
			id, Guid.NewGuid(), "ynab-1", "Checking", "budget-1",
			DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetYnabAccountMappingByIdQuery>(q => q.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(existing);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<UpdateYnabAccountMappingCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(Unit.Value);

		UpdateYnabAccountMappingRequest request = new()
		{
			YnabAccountId = "ynab-2",
			YnabAccountName = "Savings",
			YnabBudgetId = "budget-1",
		};

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateAccountMapping(id, request, CancellationToken.None);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateAccountMapping_Returns404_WhenNotFound()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.Is<GetYnabAccountMappingByIdQuery>(q => q.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabAccountMappingDto?)null);

		UpdateYnabAccountMappingRequest request = new()
		{
			YnabAccountId = "ynab-2",
			YnabAccountName = "Savings",
			YnabBudgetId = "budget-1",
		};

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateAccountMapping(id, request, CancellationToken.None);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task DeleteAccountMapping_Returns204_WhenFound()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		YnabAccountMappingDto existing = new(
			id, Guid.NewGuid(), "ynab-1", "Checking", "budget-1",
			DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetYnabAccountMappingByIdQuery>(q => q.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(existing);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<DeleteYnabAccountMappingCommand>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(Unit.Value);

		// Act
		Results<NoContent, NotFound> result = await _controller.DeleteAccountMapping(id, CancellationToken.None);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task DeleteAccountMapping_Returns404_WhenNotFound()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		_mediatorMock.Setup(m => m.Send(
			It.Is<GetYnabAccountMappingByIdQuery>(q => q.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabAccountMappingDto?)null);

		// Act
		Results<NoContent, NotFound> result = await _controller.DeleteAccountMapping(id, CancellationToken.None);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task GetAccounts_Returns200_WithActiveAccounts()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetSelectedYnabBudgetQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabBudgetSelection("budget-1"));

		List<YnabAccount> accounts =
		[
			new("acc-1", "Checking", "checking", true, false, 100000),
			new("acc-2", "Closed Account", "savings", true, true, 0),
			new("acc-3", "Savings", "savings", true, false, 50000),
		];

		_ynabClientMock.Setup(c => c.GetAccountsAsync("budget-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync(accounts);

		// Act
		Results<Ok<YnabAccountListResponse>, StatusCodeHttpResult> result =
			await _controller.GetAccounts(CancellationToken.None);

		// Assert
		Ok<YnabAccountListResponse> okResult = Assert.IsType<Ok<YnabAccountListResponse>>(result.Result);
		YnabAccountListResponse response = okResult.Value!;
		response.Data.Should().HaveCount(2); // Closed account filtered out
		response.Data.Select(a => a.Name).Should().Contain("Checking").And.Contain("Savings");
	}

	[Fact]
	public async Task GetAccounts_Returns503_WhenNotConfigured()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(false);

		// Act
		Results<Ok<YnabAccountListResponse>, StatusCodeHttpResult> result =
			await _controller.GetAccounts(CancellationToken.None);

		// Assert
		StatusCodeHttpResult statusResult = Assert.IsType<StatusCodeHttpResult>(result.Result);
		statusResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
	}

	[Fact]
	public async Task GetAccounts_ReturnsEmptyList_WhenNoBudgetSelected()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);

		_mediatorMock.Setup(m => m.Send(
			It.IsAny<GetSelectedYnabBudgetQuery>(),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabBudgetSelection(null));

		// Act
		Results<Ok<YnabAccountListResponse>, StatusCodeHttpResult> result =
			await _controller.GetAccounts(CancellationToken.None);

		// Assert
		Ok<YnabAccountListResponse> okResult = Assert.IsType<Ok<YnabAccountListResponse>>(result.Result);
		okResult.Value!.Data.Should().BeEmpty();
	}
}
