using API.Controllers.Core;
using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.Account.Create;
using Application.Commands.Account.Delete;
using Application.Commands.Account.Update;
using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.Account;
using Application.Queries.Core.Card;
using Domain.Core;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SampleData.Domain.Core;
using SampleData.Dtos.Core;

namespace Presentation.API.Tests.Controllers.Core;

public class AccountsControllerTests
{
	private readonly AccountMapper _mapper;
	private readonly CardMapper _cardMapper;
	private readonly Mock<IMediator> _mediatorMock;
	private readonly Mock<ILogger<AccountsController>> _loggerMock;
	private readonly Mock<IEntityChangeNotifier> _notifierMock;
	private readonly Mock<IAccountService> _accountServiceMock;
	private readonly AccountsController _controller;

	public AccountsControllerTests()
	{
		_mediatorMock = new Mock<IMediator>();
		_mapper = new AccountMapper();
		_cardMapper = new CardMapper();
		_loggerMock = ControllerTestHelpers.GetLoggerMock<AccountsController>();
		_notifierMock = new Mock<IEntityChangeNotifier>();
		_accountServiceMock = new Mock<IAccountService>();
		_controller = new AccountsController(_mediatorMock.Object, _mapper, _cardMapper, _loggerMock.Object, _notifierMock.Object, _accountServiceMock.Object);
		_controller.ControllerContext = new ControllerContext
		{
			HttpContext = new DefaultHttpContext()
		};
	}

	[Fact]
	public async Task GetAccountById_ReturnsOkResult_WhenAccountExists()
	{
		// Arrange
		Account account = AccountGenerator.Generate();
		AccountResponse expectedReturn = _mapper.ToResponse(account);

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAccountByIdQuery>(q => q.Id == account.Id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(account);

		// Act
		Results<Ok<AccountResponse>, NotFound> result = await _controller.GetAccountById(account.Id);

		// Assert
		Ok<AccountResponse> okResult = Assert.IsType<Ok<AccountResponse>>(result.Result);
		AccountResponse actualReturn = Assert.IsType<AccountResponse>(okResult.Value);
		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task GetAccountById_ReturnsNotFound_WhenAccountDoesNotExist()
	{
		// Arrange
		Guid missingAccountId = Guid.NewGuid();

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAccountByIdQuery>(q => q.Id == missingAccountId),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync((Account?)null);

		// Act
		Results<Ok<AccountResponse>, NotFound> result = await _controller.GetAccountById(missingAccountId);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task GetAccountById_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid id = AccountGenerator.Generate().Id;

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAccountByIdQuery>(q => q.Id == id),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetAccountById(id);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetAllAccounts_ReturnsOkResult_WithListOfAccounts()
	{
		// Arrange
		List<Account> accounts = AccountGenerator.GenerateList(2);
		List<AccountResponse> expectedReturn = [.. accounts.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllAccountsQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Account>(accounts, accounts.Count, 0, 50));

		// Act
		Results<Ok<AccountListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllAccounts(null, 0, 50, null, null);

		// Assert
		Ok<AccountListResponse> result = Assert.IsType<Ok<AccountListResponse>>(rawResult.Result);
		AccountListResponse actualReturn = result.Value!;

		actualReturn.Data.Should().BeEquivalentTo(expectedReturn);
		actualReturn.Total.Should().Be(accounts.Count);
		actualReturn.Offset.Should().Be(0);
		actualReturn.Limit.Should().Be(50);
	}

	[Fact]
	public async Task GetAllAccounts_PassesIsActiveToQuery()
	{
		// Arrange
		List<Account> accounts = AccountGenerator.GenerateList(1);
		List<AccountResponse> expectedReturn = [.. accounts.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllAccountsQuery>(q => q.Offset == 0 && q.Limit == 50 && q.IsActive == true),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Account>(accounts, accounts.Count, 0, 50));

		// Act
		Results<Ok<AccountListResponse>, BadRequest<ProblemDetails>> rawResult = await _controller.GetAllAccounts(true, 0, 50, null, null);

		// Assert
		Ok<AccountListResponse> result = Assert.IsType<Ok<AccountListResponse>>(rawResult.Result);
		AccountListResponse actualReturn = result.Value!;

		actualReturn.Data.Should().BeEquivalentTo(expectedReturn);
		actualReturn.Total.Should().Be(accounts.Count);
	}

	[Theory]
	[InlineData(-1, 50)]
	[InlineData(-100, 50)]
	public async Task GetAllAccounts_ReturnsBadRequest_WhenOffsetIsNegative(int offset, int limit)
	{
		// Act
		Results<Ok<AccountListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllAccounts(null, offset, limit, null, null);

		// Assert
		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("offset must be >= 0");
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(0, -1)]
	[InlineData(0, 501)]
	public async Task GetAllAccounts_ReturnsBadRequest_WhenLimitIsOutOfRange(int offset, int limit)
	{
		// Act
		Results<Ok<AccountListResponse>, BadRequest<ProblemDetails>> result = await _controller.GetAllAccounts(null, offset, limit, null, null);

		// Assert
		BadRequest<ProblemDetails> badRequestResult = Assert.IsType<BadRequest<ProblemDetails>>(result.Result);
		badRequestResult.Value!.Detail.Should().Be("limit must be between 1 and 500");
	}

	[Fact]
	public async Task GetAllAccounts_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		_mediatorMock.Setup(m => m.Send(
			It.Is<GetAllAccountsQuery>(q => q.Offset == 0 && q.Limit == 50),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.GetAllAccounts(null, 0, 50, null, null);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task CreateAccount_ReturnsOkResult_WithCreatedAccount()
	{
		// Arrange
		Account account = AccountGenerator.Generate();
		AccountResponse expectedReturn = _mapper.ToResponse(account);

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateAccountCommand>(c => c.Accounts.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync([account]);

		CreateAccountRequest controllerInput = AccountDtoGenerator.GenerateCreateRequest();

		// Act
		Ok<AccountResponse> result = await _controller.CreateAccount(controllerInput);

		// Assert
		AccountResponse actualReturn = result.Value!;

		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task CreateAccount_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		CreateAccountRequest controllerInput = AccountDtoGenerator.GenerateCreateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateAccountCommand>(c => c.Accounts.Count == 1),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.CreateAccount(controllerInput);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task CreateAccounts_ReturnsOkResult_WithCreatedAccounts()
	{
		// Arrange
		List<Account> accounts = AccountGenerator.GenerateList(2);
		List<AccountResponse> expectedReturn = [.. accounts.Select(_mapper.ToResponse)];

		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateAccountCommand>(c => c.Accounts.Count == accounts.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(accounts);

		List<CreateAccountRequest> controllerInput = AccountDtoGenerator.GenerateCreateRequestList(2);

		// Act
		Ok<List<AccountResponse>> result = await _controller.CreateAccounts(controllerInput);

		// Assert
		List<AccountResponse> actualReturn = result.Value!;

		actualReturn.Should().BeEquivalentTo(expectedReturn);
	}

	[Fact]
	public async Task CreateAccounts_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		List<CreateAccountRequest> controllerInput = AccountDtoGenerator.GenerateCreateRequestList(2);
		_mediatorMock.Setup(m => m.Send(
			It.Is<CreateAccountCommand>(c => c.Accounts.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.CreateAccounts(controllerInput);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task UpdateAccount_ReturnsNoContent_WhenUpdateSucceeds()
	{
		// Arrange
		UpdateAccountRequest controllerInput = AccountDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateAccountCommand>(c => c.Accounts.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateAccount(controllerInput.Id, controllerInput);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateAccount_ReturnsNotFound_WhenUpdateFails()
	{
		// Arrange
		UpdateAccountRequest controllerInput = AccountDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateAccountCommand>(c => c.Accounts.Count == 1),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateAccount(controllerInput.Id, controllerInput);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateAccount_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		UpdateAccountRequest controllerInput = AccountDtoGenerator.GenerateUpdateRequest();

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateAccountCommand>(c => c.Accounts.Count == 1),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.UpdateAccount(controllerInput.Id, controllerInput);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task UpdateAccounts_ReturnsNoContent_WhenUpdateSucceeds()
	{
		// Arrange
		List<UpdateAccountRequest> controllerInput = AccountDtoGenerator.GenerateUpdateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateAccountCommand>(c => c.Accounts.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateAccounts(controllerInput);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task UpdateAccounts_ReturnsNotFound_WhenUpdateFails()
	{
		// Arrange
		List<UpdateAccountRequest> controllerInput = AccountDtoGenerator.GenerateUpdateRequestList(2);

		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateAccountCommand>(c => c.Accounts.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound> result = await _controller.UpdateAccounts(controllerInput);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task UpdateAccounts_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		List<UpdateAccountRequest> controllerInput = AccountDtoGenerator.GenerateUpdateRequestList(2);
		_mediatorMock.Setup(m => m.Send(
			It.Is<UpdateAccountCommand>(c => c.Accounts.Count == controllerInput.Count),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.UpdateAccounts(controllerInput);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task DeleteAccount_ReturnsNoContent_WhenDeleteSucceeds()
	{
		// Arrange
		Guid id = Guid.NewGuid();

		_accountServiceMock.Setup(s => s.GetCardCountByAccountIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);
		_accountServiceMock.Setup(s => s.GetTransactionCountByAccountIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteAccountCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		// Act
		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await _controller.DeleteAccount(id);

		// Assert
		Assert.IsType<NoContent>(result.Result);
	}

	[Fact]
	public async Task DeleteAccount_ReturnsNotFound_WhenAccountDoesNotExist()
	{
		// Arrange
		Guid id = Guid.NewGuid();

		_accountServiceMock.Setup(s => s.GetCardCountByAccountIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);
		_accountServiceMock.Setup(s => s.GetTransactionCountByAccountIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteAccountCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await _controller.DeleteAccount(id);

		// Assert
		Assert.IsType<NotFound>(result.Result);
	}

	[Fact]
	public async Task DeleteAccount_ReturnsConflict_WhenCardsExist()
	{
		// Arrange
		Guid id = Guid.NewGuid();

		_accountServiceMock.Setup(s => s.GetCardCountByAccountIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(5);

		// Act
		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await _controller.DeleteAccount(id);

		// Assert
		Assert.IsType<Conflict<ProblemDetails>>(result.Result);
	}

	[Fact]
	public async Task DeleteAccount_ReturnsConflict_WhenTransactionsExist_EvenWithNoCards()
	{
		// RECEIPTS-754: transactions can outlive the card that created them, so an account
		// with zero cards can still own transactions. Deleting it must not cascade-destroy
		// them — the guard rejects the delete with 409 and never reaches the delete command.
		// Arrange
		Guid id = Guid.NewGuid();

		_accountServiceMock.Setup(s => s.GetCardCountByAccountIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);
		_accountServiceMock.Setup(s => s.GetTransactionCountByAccountIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(7);

		// Act
		Results<NoContent, NotFound, Conflict<ProblemDetails>> result = await _controller.DeleteAccount(id);

		// Assert
		Assert.IsType<Conflict<ProblemDetails>>(result.Result);
		_mediatorMock.Verify(
			m => m.Send(It.IsAny<DeleteAccountCommand>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task DeleteAccount_ThrowsException_WhenMediatorFails()
	{
		// Arrange
		Guid id = Guid.NewGuid();

		_accountServiceMock.Setup(s => s.GetCardCountByAccountIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);
		_accountServiceMock.Setup(s => s.GetTransactionCountByAccountIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(0);

		_mediatorMock.Setup(m => m.Send(
			It.Is<DeleteAccountCommand>(c => c.Id == id),
			It.IsAny<CancellationToken>()))
			.ThrowsAsync(new Exception());

		// Act
		Func<Task> act = () => _controller.DeleteAccount(id);

		// Assert
		await act.Should().ThrowAsync<Exception>();
	}

	[Fact]
	public async Task GetCardsForAccount_ReturnsOk_WithCards_WhenAccountExists()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		List<Card> cards = CardGenerator.GenerateList(3);

		_accountServiceMock
			.Setup(s => s.ExistsAsync(accountId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		_mediatorMock
			.Setup(m => m.Send(
				It.Is<GetCardsByAccountIdQuery>(q => q.AccountId == accountId),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(cards);

		// Act
		Results<Ok<List<CardResponse>>, NotFound> result = await _controller.GetCardsForAccount(accountId);

		// Assert
		Ok<List<CardResponse>> ok = Assert.IsType<Ok<List<CardResponse>>>(result.Result);
		List<CardResponse> responses = Assert.IsType<List<CardResponse>>(ok.Value);
		responses.Should().HaveCount(3);
		responses.Select(r => r.Id).Should().BeEquivalentTo(cards.Select(c => c.Id));
	}

	[Fact]
	public async Task GetCardsForAccount_ReturnsNotFound_WhenAccountDoesNotExist()
	{
		// Arrange
		Guid missingId = Guid.NewGuid();

		_accountServiceMock
			.Setup(s => s.ExistsAsync(missingId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		// Act
		Results<Ok<List<CardResponse>>, NotFound> result = await _controller.GetCardsForAccount(missingId);

		// Assert
		Assert.IsType<NotFound>(result.Result);
		_mediatorMock.Verify(
			m => m.Send(It.IsAny<GetCardsByAccountIdQuery>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task GetCardsForAccount_ReturnsEmptyList_WhenAccountHasNoCards()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();

		_accountServiceMock
			.Setup(s => s.ExistsAsync(accountId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		_mediatorMock
			.Setup(m => m.Send(
				It.Is<GetCardsByAccountIdQuery>(q => q.AccountId == accountId),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		// Act
		Results<Ok<List<CardResponse>>, NotFound> result = await _controller.GetCardsForAccount(accountId);

		// Assert
		Ok<List<CardResponse>> ok = Assert.IsType<Ok<List<CardResponse>>>(result.Result);
		ok.Value.Should().BeEmpty();
	}
}
