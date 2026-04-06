using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetYnabCategoryMappingByIdQueryHandlerTests
{
	[Fact]
	public async Task Handle_ReturnsMapping_WhenFound()
	{
		// Arrange
		Mock<IYnabCategoryMappingService> mockService = new();
		GetYnabCategoryMappingByIdQueryHandler handler = new(mockService.Object);
		Guid id = Guid.NewGuid();

		YnabCategoryMappingDto expected = new(id, "Groceries", "cat-1", "Groceries", "Needs", "budget-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
		mockService.Setup(s => s.GetByIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		// Act
		YnabCategoryMappingDto? result = await handler.Handle(new GetYnabCategoryMappingByIdQuery(id), CancellationToken.None);

		// Assert
		result.Should().NotBeNull();
		result.Should().Be(expected);
	}

	[Fact]
	public async Task Handle_ReturnsNull_WhenNotFound()
	{
		// Arrange
		Mock<IYnabCategoryMappingService> mockService = new();
		GetYnabCategoryMappingByIdQueryHandler handler = new(mockService.Object);
		Guid id = Guid.NewGuid();

		mockService.Setup(s => s.GetByIdAsync(id, It.IsAny<CancellationToken>()))
			.ReturnsAsync((YnabCategoryMappingDto?)null);

		// Act
		YnabCategoryMappingDto? result = await handler.Handle(new GetYnabCategoryMappingByIdQuery(id), CancellationToken.None);

		// Assert
		result.Should().BeNull();
	}
}
