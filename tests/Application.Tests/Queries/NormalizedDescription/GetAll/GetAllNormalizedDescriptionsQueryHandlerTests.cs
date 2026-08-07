using Application.Interfaces.Services;
using Application.Models;
using Application.Models.NormalizedDescriptions;
using Application.Queries.NormalizedDescription.GetAll;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.NormalizedDescription.GetAll;

public class GetAllNormalizedDescriptionsQueryHandlerTests
{
	// The filter is a set since RECEIPTS-878. These stand in for the single-status cases so the
	// tests read the way they did before, and MultiStatus covers the case the set exists for.
	private static readonly NormalizedDescriptionStatus[] PendingOnly = [NormalizedDescriptionStatus.PendingReview];
	private static readonly NormalizedDescriptionStatus[] ActiveOnly = [NormalizedDescriptionStatus.Active];
	private static readonly NormalizedDescriptionStatus[] MergeTargets =
		[NormalizedDescriptionStatus.Active, NormalizedDescriptionStatus.PendingReview];

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

	private static PagedResult<NormalizedDescriptionDetail> Page(
		List<NormalizedDescriptionDetail> data,
		int? total = null,
		int offset = 0,
		int limit = 50) =>
		new(data, total ?? data.Count, offset, limit);

	[Fact]
	public async Task Handle_NoFilter_ReturnsPageFromService()
	{
		// Arrange
		Mock<INormalizedDescriptionService> mockService = new();
		PagedResult<NormalizedDescriptionDetail> expected = Page(
		[
			Detail("coffee beans", NormalizedDescriptionStatus.Active),
			Detail("whole milk", NormalizedDescriptionStatus.PendingReview),
		]);

		mockService
			.Setup(s => s.GetAllAsync(null, null, 0, 50, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetAllNormalizedDescriptionsQueryHandler handler = new(mockService.Object);
		GetAllNormalizedDescriptionsQuery query = new(StatusFilter: null, Q: null, Offset: 0, Limit: 50);

		// Act
		PagedResult<NormalizedDescriptionDetail> actual = await handler.Handle(query, CancellationToken.None);

		// Assert
		actual.Should().BeSameAs(expected);
		mockService.Verify(s => s.GetAllAsync(null, null, 0, 50, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_WithFilter_ForwardsToService()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		PagedResult<NormalizedDescriptionDetail> expected = Page(
		[
			Detail("whole milk", NormalizedDescriptionStatus.PendingReview),
		]);

		mockService
			.Setup(s => s.GetAllAsync(PendingOnly, null, 0, 50, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetAllNormalizedDescriptionsQueryHandler handler = new(mockService.Object);
		GetAllNormalizedDescriptionsQuery query = new(PendingOnly, null, 0, 50);

		PagedResult<NormalizedDescriptionDetail> actual = await handler.Handle(query, CancellationToken.None);

		actual.Should().BeSameAs(expected);
		mockService.Verify(s => s.GetAllAsync(PendingOnly, null, 0, 50, It.IsAny<CancellationToken>()), Times.Once);
	}

	// The paging window and the search term are the whole point of RECEIPTS-879, and the handler is
	// the only thing between the controller's validated inputs and the service. A handler that
	// dropped either would silently serve page 1 forever.
	[Fact]
	public async Task Handle_ForwardsSearchTermAndPagingWindow()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		mockService
			.Setup(s => s.GetAllAsync(It.IsAny<IReadOnlyCollection<NormalizedDescriptionStatus>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Page([], total: 0, offset: 100, limit: 25));

		GetAllNormalizedDescriptionsQueryHandler handler = new(mockService.Object);

		await handler.Handle(new GetAllNormalizedDescriptionsQuery(null, "milk", 100, 25), CancellationToken.None);

		mockService.Verify(s => s.GetAllAsync(null, "milk", 100, 25, It.IsAny<CancellationToken>()), Times.Once);
	}

	// RECEIPTS-878. The merge dialog asks for Active and PendingReview in one call; a handler that
	// collapsed the set to its first element would quietly drop half the candidates.
	[Fact]
	public async Task Handle_ForwardsEveryStatusInTheFilter()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		mockService
			.Setup(s => s.GetAllAsync(It.IsAny<IReadOnlyCollection<NormalizedDescriptionStatus>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Page([]));

		GetAllNormalizedDescriptionsQueryHandler handler = new(mockService.Object);

		await handler.Handle(new GetAllNormalizedDescriptionsQuery(MergeTargets, null, 0, 50), CancellationToken.None);

		mockService.Verify(
			s => s.GetAllAsync(
				It.Is<IReadOnlyCollection<NormalizedDescriptionStatus>?>(f =>
					f != null &&
					f.Count == 2 &&
					f.Contains(NormalizedDescriptionStatus.Active) &&
					f.Contains(NormalizedDescriptionStatus.PendingReview)),
				null,
				0,
				50,
				It.IsAny<CancellationToken>()),
			Times.Once);
	}

	// Total is the count of matching rows, not the page length — a client cannot render pagination
	// controls from a total that collapses to the number of rows it happens to be holding.
	[Fact]
	public async Task Handle_PreservesTotalDistinctFromPageLength()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		mockService
			.Setup(s => s.GetAllAsync(It.IsAny<IReadOnlyCollection<NormalizedDescriptionStatus>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Page([Detail("coffee beans", NormalizedDescriptionStatus.Active)], total: 843, offset: 50, limit: 1));

		GetAllNormalizedDescriptionsQueryHandler handler = new(mockService.Object);

		PagedResult<NormalizedDescriptionDetail> actual =
			await handler.Handle(new GetAllNormalizedDescriptionsQuery(null, null, 50, 1), CancellationToken.None);

		actual.Data.Should().ContainSingle();
		actual.Total.Should().Be(843);
		actual.Offset.Should().Be(50);
		actual.Limit.Should().Be(1);
	}

	[Fact]
	public async Task Handle_EmptyResult_ReturnsEmptyPage()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		mockService
			.Setup(s => s.GetAllAsync(It.IsAny<IReadOnlyCollection<NormalizedDescriptionStatus>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Page([]));

		GetAllNormalizedDescriptionsQueryHandler handler = new(mockService.Object);
		GetAllNormalizedDescriptionsQuery query = new(ActiveOnly, null, 0, 50);

		PagedResult<NormalizedDescriptionDetail> actual = await handler.Handle(query, CancellationToken.None);

		actual.Data.Should().BeEmpty();
		actual.Total.Should().Be(0);
	}

	// The handler is a pass-through, so the evidence a reviewer acts on (RECEIPTS-873) must
	// survive it untouched — this guards against a future "simplification" that re-projects
	// to the bare domain model and silently drops the near-miss and linked-item count.
	[Fact]
	public async Task Handle_PreservesReviewEvidence()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		PagedResult<NormalizedDescriptionDetail> expected = Page(
		[
			Detail("strawberry preserves", NormalizedDescriptionStatus.PendingReview, linkedItemCount: 4, nearestNeighbourName: "Strawberry Jam", nearestNeighbourSimilarity: 0.86),
		]);

		mockService
			.Setup(s => s.GetAllAsync(It.IsAny<IReadOnlyCollection<NormalizedDescriptionStatus>?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		GetAllNormalizedDescriptionsQueryHandler handler = new(mockService.Object);

		PagedResult<NormalizedDescriptionDetail> actual =
			await handler.Handle(new GetAllNormalizedDescriptionsQuery(PendingOnly, null, 0, 50), CancellationToken.None);

		actual.Data.Should().ContainSingle();
		actual.Data[0].LinkedItemCount.Should().Be(4);
		actual.Data[0].NearestNeighbourName.Should().Be("Strawberry Jam");
		actual.Data[0].Description.NearestNeighbourSimilarity.Should().Be(0.86);
	}
}
