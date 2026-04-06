using Application.Commands.Ynab.AccountMapping;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class CreateYnabAccountMappingCommandHandlerTests
{
	private readonly Mock<IYnabAccountMappingService> _mappingServiceMock = new();
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
	public async Task Handle_WhenAccountDoesNotExist_ThrowsInvalidOperationException()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		_accountServiceMock.Setup(s => s.ExistsAsync(accountId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		CreateYnabAccountMappingCommand command = new(accountId, "ynab-acc-1", "My Checking", "budget-1");

		// Act
		Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage($"Account with ID '{accountId}' does not exist.");

		_mappingServiceMock.Verify(s => s.CreateAsync(
			It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}
}
