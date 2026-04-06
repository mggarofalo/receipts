using Application.Commands.Ynab.AccountMapping;
using Application.Interfaces.Services;
using FluentAssertions;
using MediatR;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class DeleteYnabAccountMappingCommandHandlerTests
{
	[Fact]
	public async Task Handle_DelegatesToService()
	{
		// Arrange
		Mock<IYnabAccountMappingService> mockService = new();
		DeleteYnabAccountMappingCommandHandler handler = new(mockService.Object);
		Guid id = Guid.NewGuid();

		DeleteYnabAccountMappingCommand command = new(id);

		// Act
		Unit result = await handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().Be(Unit.Value);
		mockService.Verify(s => s.DeleteAsync(id, It.IsAny<CancellationToken>()), Times.Once);
	}
}
