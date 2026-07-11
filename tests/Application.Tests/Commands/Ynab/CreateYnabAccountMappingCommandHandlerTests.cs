using Application.Commands.Ynab.AccountMapping;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class CreateYnabAccountMappingCommandHandlerTests
{
	private readonly Mock<IYnabAccountMappingService> _mappingServiceMock = new();
	// RECEIPTS-751: the handler validates ReceiptsAccountId (an FK to Accounts) against
	// IAccountService, not ICardService.
	private readonly Mock<IAccountService> _accountServiceMock = new();
	private readonly CreateYnabAccountMappingCommandHandler _handler;

	public CreateYnabAccountMappingCommandHandlerTests()
	{
		_handler = new CreateYnabAccountMappingCommandHandler(
			_mappingServiceMock.Object,
			_accountServiceMock.Object);
	}

	[Fact]
	public async Task Handle_WhenAccountExists_CreatesMapping()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		string ynabAccountId = "ynab-acc-1";
		string ynabAccountName = "My Checking";
		string ynabBudgetId = "budget-1";

		_accountServiceMock.Setup(s => s.ExistsAsync(accountId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		YnabAccountMappingDto expected = new(
			Guid.NewGuid(), accountId, ynabAccountId, ynabAccountName, ynabBudgetId,
			DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

		_mappingServiceMock.Setup(s => s.CreateAsync(
			accountId, ynabAccountId, ynabAccountName, ynabBudgetId,
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		CreateYnabAccountMappingCommand command = new(accountId, ynabAccountId, ynabAccountName, ynabBudgetId);

		// Act
		YnabAccountMappingDto result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expected);
		_accountServiceMock.Verify(s => s.ExistsAsync(accountId, It.IsAny<CancellationToken>()), Times.Once);
		_mappingServiceMock.Verify(s => s.CreateAsync(
			accountId, ynabAccountId, ynabAccountName, ynabBudgetId,
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_WhenAccountDoesNotExist_ThrowsArgumentException()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		_accountServiceMock.Setup(s => s.ExistsAsync(accountId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		CreateYnabAccountMappingCommand command = new(accountId, "ynab-acc-1", "My Checking", "budget-1");

		// Act
		Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<ArgumentException>()
			.WithMessage($"Account with ID '{accountId}' does not exist.*");

		_mappingServiceMock.Verify(s => s.CreateAsync(
			It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}

	// RECEIPTS-751 regression: an Account created after the account/card split has no Card
	// sharing its Guid. Validation must consult Accounts (IAccountService.ExistsAsync), which
	// returns true here, so mapping succeeds. Before the fix this validated against Cards and
	// 400'd with "Account ... does not exist" even though the Account existed.
	[Fact]
	public async Task Handle_WhenAccountHasNoMatchingCard_ValidatesAgainstAccountsAndCreatesMapping()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		string ynabAccountId = "ynab-acc-2";
		string ynabAccountName = "New Account";
		string ynabBudgetId = "budget-2";

		// Account exists (no Card shares its id — the account/card-split scenario).
		_accountServiceMock.Setup(s => s.ExistsAsync(accountId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		YnabAccountMappingDto expected = new(
			Guid.NewGuid(), accountId, ynabAccountId, ynabAccountName, ynabBudgetId,
			DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

		_mappingServiceMock.Setup(s => s.CreateAsync(
			accountId, ynabAccountId, ynabAccountName, ynabBudgetId,
			It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		CreateYnabAccountMappingCommand command = new(accountId, ynabAccountId, ynabAccountName, ynabBudgetId);

		// Act
		YnabAccountMappingDto result = await _handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expected);
		_accountServiceMock.Verify(s => s.ExistsAsync(accountId, It.IsAny<CancellationToken>()), Times.Once);
		_mappingServiceMock.Verify(s => s.CreateAsync(
			accountId, ynabAccountId, ynabAccountName, ynabBudgetId,
			It.IsAny<CancellationToken>()), Times.Once);
	}
}
