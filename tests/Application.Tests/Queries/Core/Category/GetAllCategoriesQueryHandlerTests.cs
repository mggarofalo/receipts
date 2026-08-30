using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.Category;
using FluentAssertions;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Queries.Core.Category;

public class GetAllCategoriesQueryHandlerTests
{
	[Fact]
	public async Task Handle_ShouldReturnAllCategories()
	{
		List<Domain.Core.Category> expected = CategoryGenerator.GenerateList(2);

		Mock<ICategoryService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), true, "food", It.IsAny<CancellationToken>())).ReturnsAsync(new PagedResult<Domain.Core.Category>(expected, expected.Count, 0, 50));

		GetAllCategoriesQueryHandler handler = new(mockService.Object);
		GetAllCategoriesQuery query = new(0, 50, SortParams.Default, true, "food");

		PagedResult<Domain.Core.Category> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.VerifyAll();
	}
}
