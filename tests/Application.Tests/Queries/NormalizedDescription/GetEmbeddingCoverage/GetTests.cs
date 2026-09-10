using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Application.Queries.NormalizedDescription.GetEmbeddingCoverage;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.NormalizedDescription.GetEmbeddingCoverage;

public class GetEmbeddingCoverageQueryHandlerTests
{
	[Fact]
	public async Task Handle_ReturnsCurrentFingerprintCoverageFromService()
	{
		Mock<INormalizedDescriptionService> service = new();
		EmbeddingCoverage expected = new(
			"space-fingerprint",
			CanonicalReady: 8,
			CanonicalTotal: 10,
			ItemReady: 18,
			ItemTotal: 20);
		service.Setup(s => s.GetEmbeddingCoverageAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);
		GetEmbeddingCoverageQueryHandler handler = new(service.Object);

		EmbeddingCoverage actual = await handler.Handle(
			new GetEmbeddingCoverageQuery(), CancellationToken.None);

		actual.Should().BeSameAs(expected);
		actual.CanonicalPending.Should().Be(2);
		actual.ItemPending.Should().Be(2);
		actual.IsComplete.Should().BeFalse();
		service.Verify(s => s.GetEmbeddingCoverageAsync(It.IsAny<CancellationToken>()), Times.Once);
	}
}
