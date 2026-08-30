using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.Card;
using FluentAssertions;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Queries.Core.Card;

public class GetAllCardsQueryHandlerTests
{
	[Fact]
	public async Task Handle_ShouldReturnAllAccounts()
	{
		List<Domain.Core.Card> expected = CardGenerator.GenerateList(2);

		Mock<ICardService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), false, "4242", It.IsAny<CancellationToken>())).ReturnsAsync(new PagedResult<Domain.Core.Card>(expected, expected.Count, 0, 50));

		GetAllCardsQueryHandler handler = new(mockService.Object);
		GetAllCardsQuery query = new(0, 50, SortParams.Default, false, "4242");

		PagedResult<Domain.Core.Card> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.VerifyAll();
	}
}
