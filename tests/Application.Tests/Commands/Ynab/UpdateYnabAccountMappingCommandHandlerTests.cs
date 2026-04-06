using Application.Commands.Ynab.AccountMapping;
using Application.Interfaces.Services;
using FluentAssertions;
using MediatR;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class UpdateYnabAccountMappingCommandHandlerTests
{
	[Fact]
	public async Task Handle_DelegatesToService()
	{
		// Arrange
		Mock<IYnabAccountMappingService> mockService = new();
		UpdateYnabAccountMappingCommandHandler handler = new(mockService.Object);
		Guid id = Guid.NewGuid();

		UpdateYnabAccountMappingCommand command = new(id, "ynab-acc-2", "Savings", "budget-1");

		// Act
		Unit result = await handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().Be(Unit.Value);
		mockService.Verify(s => s.UpdateAsync(
			id, "ynab-acc-2", "Savings", "budget-1",
			It.IsAny<CancellationToken>()), Times.Once);
	}
}
