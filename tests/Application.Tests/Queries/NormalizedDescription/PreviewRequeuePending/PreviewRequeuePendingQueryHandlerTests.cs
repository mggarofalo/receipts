using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Application.Queries.NormalizedDescription.PreviewRequeuePending;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.NormalizedDescription.PreviewRequeuePending;

public class PreviewRequeuePendingQueryHandlerTests
{
	[Fact]
	public async Task Handle_ForwardsTheServicePreview()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		mockService
			.Setup(s => s.PreviewRequeuePendingAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RequeuePendingPreview(4, 120, 118, 3, 90));

		PreviewRequeuePendingQueryHandler handler = new(mockService.Object);

		RequeuePendingPreview preview = await handler.Handle(new PreviewRequeuePendingQuery(), CancellationToken.None);

		preview.PendingDescriptionCount.Should().Be(4);
		preview.LinkedItemCount.Should().Be(120);
		preview.StaleMatchScoreCount.Should().Be(118);
		preview.EstimatedResolverCycles.Should().Be(3);
		preview.EstimatedCatchUpSeconds.Should().Be(90);
		mockService.Verify(s => s.PreviewRequeuePendingAsync(It.IsAny<CancellationToken>()), Times.Once);
	}
}
