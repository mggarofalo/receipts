using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Application.Queries.NormalizedDescription.GetAll;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.NormalizedDescription.GetAll;

public class GetAllNormalizedDescriptionsQueryHandlerTests
{
	private static NormalizedDescriptionDetail Detail(
		string canonicalName,
		NormalizedDescriptionStatus status,
		int linkedItemCount = 0,
		string? nearestNeighbourName = null,
		double? nearestNeighbourSimilarity = null) =>
		new(
			new Domain.NormalizedDescriptions.NormalizedDescription(
				Guid.NewGuid(),
				canonicalName,
				status,
				DateTimeOffset.UtcNow,
				nearestNeighbourSimilarity is null ? null : Guid.NewGuid(),
				nearestNeighbourSimilarity),
			linkedItemCount,
			nearestNeighbourName,
			[]);

	[Fact]
	public async Task Handle_NoFilter_ReturnsAllFromService()
	{
		// Arrange
		Mock<INormalizedDescriptionService> mockService = new();
		List<NormalizedDescriptionDetail> expected =
		[
			Detail("coffee beans", NormalizedDescriptionStatus.Active),
			Detail("whole milk", NormalizedDescriptionStatus.PendingReview),
		];

		mockService
			.Setup(s => s.GetAllAsync(null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetAllNormalizedDescriptionsQueryHandler handler = new(mockService.Object);
		GetAllNormalizedDescriptionsQuery query = new(StatusFilter: null);

		// Act
		List<NormalizedDescriptionDetail> actual = await handler.Handle(query, CancellationToken.None);

		// Assert
		actual.Should().BeSameAs(expected);
		mockService.Verify(s => s.GetAllAsync(null, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_WithFilter_ForwardsToService()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		List<NormalizedDescriptionDetail> expected =
		[
			Detail("whole milk", NormalizedDescriptionStatus.PendingReview),
		];

		mockService
			.Setup(s => s.GetAllAsync(NormalizedDescriptionStatus.PendingReview, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetAllNormalizedDescriptionsQueryHandler handler = new(mockService.Object);
		GetAllNormalizedDescriptionsQuery query = new(NormalizedDescriptionStatus.PendingReview);

		List<NormalizedDescriptionDetail> actual = await handler.Handle(query, CancellationToken.None);

		actual.Should().BeSameAs(expected);
		mockService.Verify(s => s.GetAllAsync(NormalizedDescriptionStatus.PendingReview, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_EmptyResult_ReturnsEmptyList()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		mockService
			.Setup(s => s.GetAllAsync(It.IsAny<NormalizedDescriptionStatus?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync([]);

		GetAllNormalizedDescriptionsQueryHandler handler = new(mockService.Object);
		GetAllNormalizedDescriptionsQuery query = new(NormalizedDescriptionStatus.Active);

		List<NormalizedDescriptionDetail> actual = await handler.Handle(query, CancellationToken.None);

		actual.Should().BeEmpty();
	}

	// The handler is a pass-through, so the evidence a reviewer acts on (RECEIPTS-873) must
	// survive it untouched — this guards against a future "simplification" that re-projects
	// to the bare domain model and silently drops the near-miss and linked-item count.
	[Fact]
	public async Task Handle_PreservesReviewEvidence()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		List<NormalizedDescriptionDetail> expected =
		[
			Detail("strawberry preserves", NormalizedDescriptionStatus.PendingReview, linkedItemCount: 4, nearestNeighbourName: "Strawberry Jam", nearestNeighbourSimilarity: 0.86),
		];

		mockService
			.Setup(s => s.GetAllAsync(It.IsAny<NormalizedDescriptionStatus?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetAllNormalizedDescriptionsQueryHandler handler = new(mockService.Object);

		List<NormalizedDescriptionDetail> actual = await handler.Handle(new GetAllNormalizedDescriptionsQuery(NormalizedDescriptionStatus.PendingReview), CancellationToken.None);

		actual.Should().ContainSingle();
		actual[0].LinkedItemCount.Should().Be(4);
		actual[0].NearestNeighbourName.Should().Be("Strawberry Jam");
		actual[0].Description.NearestNeighbourSimilarity.Should().Be(0.86);
	}
}
