using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.ItemTemplate;
using FluentAssertions;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Queries.Core.ItemTemplate;

public class GetAllItemTemplatesQueryHandlerTests
{
	[Fact]
	public async Task Handle_ShouldReturnAllItemTemplates()
	{
		List<Domain.Core.ItemTemplate> expected = ItemTemplateGenerator.GenerateList(2);

		Mock<IItemTemplateService> mockService = new();
		// The handler always takes the search path (RECEIPTS-930); a null term is the unfiltered
		// list, so there is no second code path to keep in step.
		mockService.Setup(r => r.SearchAsync(null, 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PagedResult<Domain.Core.ItemTemplate>(expected, expected.Count, 0, 50));

		GetAllItemTemplatesQueryHandler handler = new(mockService.Object);
		GetAllItemTemplatesQuery query = new(0, 50, SortParams.Default);

		PagedResult<Domain.Core.ItemTemplate> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
	}

	[Fact]
	public async Task Handle_ForwardsTheSearchTerm()
	{
		List<Domain.Core.ItemTemplate> expected = ItemTemplateGenerator.GenerateList(1);

		Mock<IItemTemplateService> mockService = new();
		mockService.Setup(r => r.SearchAsync("milk", 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PagedResult<Domain.Core.ItemTemplate>(expected, 1, 0, 50));

		GetAllItemTemplatesQueryHandler handler = new(mockService.Object);
		GetAllItemTemplatesQuery query = new(0, 50, SortParams.Default, "milk");

		PagedResult<Domain.Core.ItemTemplate> result = await handler.Handle(query, CancellationToken.None);

		// Dropping the term here would silently hand the picker the unfiltered first page, which is
		// exactly the truncation the server-side search exists to remove.
		result.Data.Should().BeSameAs(expected);
		mockService.Verify(r => r.SearchAsync("milk", 0, 50, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()), Times.Once);
	}
}
