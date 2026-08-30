using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.Subcategory;
using FluentAssertions;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Queries.Core.Subcategory;

public class GetSubcategoriesByCategoryIdQueryHandlerTests
{
	[Fact]
	public async Task Handle_ShouldReturnSubcategories_WhenCategoryHasSubcategories()
	{
		List<Domain.Core.Subcategory> expected = SubcategoryGenerator.GenerateList(2);
		Guid categoryId = Guid.NewGuid();

		Mock<ISubcategoryService> mockService = new();
		mockService.Setup(r => r.GetByCategoryIdAsync(categoryId, 0, 50, It.IsAny<SortParams>(), true, "dairy", It.IsAny<CancellationToken>())).ReturnsAsync(new PagedResult<Domain.Core.Subcategory>(expected, expected.Count, 0, 50));

		GetSubcategoriesByCategoryIdQueryHandler handler = new(mockService.Object);
		GetSubcategoriesByCategoryIdQuery query = new(categoryId, 0, 50, SortParams.Default, true, "dairy");

		PagedResult<Domain.Core.Subcategory> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.VerifyAll();
	}

	[Fact]
	public async Task Handle_ShouldReturnEmptyList_WhenCategoryHasNoSubcategories()
	{
		Guid categoryId = Guid.NewGuid();

		Mock<ISubcategoryService> mockService = new();
		mockService.Setup(r => r.GetByCategoryIdAsync(categoryId, 0, 50, It.IsAny<SortParams>(), It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PagedResult<Domain.Core.Subcategory>([], 0, 0, 50));

		GetSubcategoriesByCategoryIdQueryHandler handler = new(mockService.Object);
		GetSubcategoriesByCategoryIdQuery query = new(categoryId, 0, 50, SortParams.Default);

		PagedResult<Domain.Core.Subcategory> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeEmpty();
	}
}
