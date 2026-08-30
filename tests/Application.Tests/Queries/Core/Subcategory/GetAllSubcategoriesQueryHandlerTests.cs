using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.Subcategory;
using FluentAssertions;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Queries.Core.Subcategory;

public class GetAllSubcategoriesQueryHandlerTests
{
	[Fact]
	public async Task Handle_ShouldReturnAllSubcategories()
	{
		List<Domain.Core.Subcategory> expected = SubcategoryGenerator.GenerateList(2);

		Mock<ISubcategoryService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), true, "dairy", It.IsAny<CancellationToken>())).ReturnsAsync(new PagedResult<Domain.Core.Subcategory>(expected, expected.Count, 0, 50));

		GetAllSubcategoriesQueryHandler handler = new(mockService.Object);
		GetAllSubcategoriesQuery query = new(0, 50, SortParams.Default, true, "dairy");

		PagedResult<Domain.Core.Subcategory> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.VerifyAll();
	}
}
