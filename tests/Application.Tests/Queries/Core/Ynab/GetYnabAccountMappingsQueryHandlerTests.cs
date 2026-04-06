using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetYnabAccountMappingsQueryHandlerTests
{
	[Fact]
	public async Task Handle_ReturnsMappingsFromService()
	{
		// Arrange
		List<YnabAccountMappingDto> expected =
		[
			new(Guid.NewGuid(), Guid.NewGuid(), "ynab-1", "Checking", "budget-1",
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			new(Guid.NewGuid(), Guid.NewGuid(), "ynab-2", "Savings", "budget-1",
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
		];

		Mock<IYnabAccountMappingService> mockService = new();
		mockService.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetYnabAccountMappingsQueryHandler handler = new(mockService.Object);

		// Act
		List<YnabAccountMappingDto> result = await handler.Handle(new GetYnabAccountMappingsQuery(), CancellationToken.None);

		// Assert
		result.Should().BeSameAs(expected);
	}
}
