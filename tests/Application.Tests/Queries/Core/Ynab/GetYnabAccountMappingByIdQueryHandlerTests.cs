using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetYnabAccountMappingByIdQueryHandlerTests
{
	[Fact]
	public async Task Handle_ReturnsMapping_WhenFound()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		YnabAccountMappingDto expected = new(
			id, Guid.NewGuid(), "ynab-1", "Checking", "budget-1",
			DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

		Mock<IYnabAccountMappingService> mockService = new();
		mockService.Setup(s => s.GetByIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetYnabAccountMappingByIdQueryHandler handler = new(mockService.Object);

		// Act
		YnabAccountMappingDto? result = await handler.Handle(new GetYnabAccountMappingByIdQuery(id), CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expected);
	}

	[Fact]
	public async Task Handle_ReturnsNull_WhenNotFound()
	{
		// Arrange
		Guid id = Guid.NewGuid();
		Mock<IYnabAccountMappingService> mockService = new();
		mockService.Setup(s => s.GetByIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabAccountMappingDto?)null);

		GetYnabAccountMappingByIdQueryHandler handler = new(mockService.Object);

		// Act
		YnabAccountMappingDto? result = await handler.Handle(new GetYnabAccountMappingByIdQuery(id), CancellationToken.None);

		// Assert
		result.Should().BeNull();
	}
}
