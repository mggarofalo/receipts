using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetAllYnabCategoryMappingsQueryHandlerTests
{
	[Fact]
	public async Task Handle_ReturnsAllMappings()
	{
		// Arrange
		Mock<IYnabCategoryMappingService> mockService = new();
		GetAllYnabCategoryMappingsQueryHandler handler = new(mockService.Object);

		List<YnabCategoryMappingDto> mappings =
		[
			new(Guid.NewGuid(), "Groceries", "cat-1", "Groceries", "Needs", "budget-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			new(Guid.NewGuid(), "Electronics", "cat-2", "Technology", "Wants", "budget-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
		];

		mockService.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(mappings);

		// Act
		List<YnabCategoryMappingDto> result = await handler.Handle(new GetAllYnabCategoryMappingsQuery(), CancellationToken.None);

		// Assert
		result.Should().HaveCount(2);
		result.Should().BeEquivalentTo(mappings);
	}

	[Fact]
	public async Task Handle_ReturnsEmptyList_WhenNoMappings()
	{
		// Arrange
		Mock<IYnabCategoryMappingService> mockService = new();
		GetAllYnabCategoryMappingsQueryHandler handler = new(mockService.Object);

		mockService.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		// Act
		List<YnabCategoryMappingDto> result = await handler.Handle(new GetAllYnabCategoryMappingsQuery(), CancellationToken.None);

		// Assert
		result.Should().BeEmpty();
	}
}
