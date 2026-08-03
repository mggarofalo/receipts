using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.ItemTemplate.GetHistoryCandidates;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.ItemTemplate.GetHistoryCandidates;

public class GetItemTemplateHistoryCandidatesQueryHandlerTests
{
	private readonly Mock<IItemTemplateHistoryCandidateService> _serviceMock = new();

	[Fact]
	public async Task Handle_ShouldReturnCandidatesFromService()
	{
		// Arrange
		List<ItemTemplateHistoryCandidate> expected =
		[
			new()
			{
				Name = "Whole Milk",
				OccurrenceCount = 5,
				LastPurchasedAt = new DateOnly(2026, 1, 15),
				SuggestedCategory = "Groceries",
				SuggestedSubcategory = "Dairy",
				SuggestedUnitPrice = 3.99m,
				SuggestedItemCode = "MILK-001",
			},
		];

		PagedResult<ItemTemplateHistoryCandidate> paged = new(expected, 1, 0, 50);
		_serviceMock
			.Setup(s => s.GetHistoryCandidatesAsync(0, 50, 2, It.IsAny<CancellationToken>()))
			.ReturnsAsync(paged);

		GetItemTemplateHistoryCandidatesQueryHandler handler = new(_serviceMock.Object);
		GetItemTemplateHistoryCandidatesQuery query = new(0, 50, 2);

		// Act
		PagedResult<ItemTemplateHistoryCandidate> result = await handler.Handle(query, CancellationToken.None);

		// Assert
		result.Should().BeSameAs(paged);
		result.Data.Should().BeSameAs(expected);
	}

	[Fact]
	public async Task Handle_ShouldForwardPagingAndMinCountToService()
	{
		// Arrange
		_serviceMock
			.Setup(s => s.GetHistoryCandidatesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ItemTemplateHistoryCandidate>([], 0, 20, 10));

		GetItemTemplateHistoryCandidatesQueryHandler handler = new(_serviceMock.Object);
		GetItemTemplateHistoryCandidatesQuery query = new(20, 10, 4);

		// Act
		await handler.Handle(query, CancellationToken.None);

		// Assert
		_serviceMock.Verify(
			s => s.GetHistoryCandidatesAsync(20, 10, 4, It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task Handle_ShouldReturnEmptyPage_WhenServiceHasNoCandidates()
	{
		// Arrange
		_serviceMock
			.Setup(s => s.GetHistoryCandidatesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<ItemTemplateHistoryCandidate>([], 0, 0, 50));

		GetItemTemplateHistoryCandidatesQueryHandler handler = new(_serviceMock.Object);

		// Act
		PagedResult<ItemTemplateHistoryCandidate> result = await handler.Handle(new GetItemTemplateHistoryCandidatesQuery(0, 50, 2), CancellationToken.None);

		// Assert
		result.Data.Should().BeEmpty();
		result.Total.Should().Be(0);
	}

	[Fact]
	public async Task Handle_ShouldPropagateException_WhenServiceThrows()
	{
		// Arrange
		_serviceMock
			.Setup(s => s.GetHistoryCandidatesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("boom"));

		GetItemTemplateHistoryCandidatesQueryHandler handler = new(_serviceMock.Object);

		// Act
		Func<Task> act = async () => await handler.Handle(new GetItemTemplateHistoryCandidatesQuery(0, 50, 2), CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>();
	}
}
