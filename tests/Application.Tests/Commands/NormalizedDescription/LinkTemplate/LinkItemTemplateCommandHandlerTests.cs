using Application.Commands.NormalizedDescription.LinkTemplate;
using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.NormalizedDescription.LinkTemplate;

public class LinkItemTemplateCommandHandlerTests
{
	private static NormalizedDescriptionDetail Detail(Guid id) => new(
		new Domain.NormalizedDescriptions.NormalizedDescription(
			id,
			"Gallon of Milk",
			NormalizedDescriptionStatus.Active,
			DateTimeOffset.UtcNow,
			nearestNeighbourId: null,
			nearestNeighbourSimilarity: null,
			displayLabel: null),
		LinkedItemCount: 4,
		NearestNeighbourName: null,
		["Gallon of Milk"]);

	[Fact]
	public async Task Handle_ForwardsBothIdsAndReturnsWhatHappened()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		Guid descriptionId = Guid.NewGuid();
		Guid templateId = Guid.NewGuid();
		Guid survivorId = Guid.NewGuid();

		mockService
			.Setup(s => s.LinkTemplateAsync(descriptionId, templateId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new LinkTemplateResult(Detail(survivorId), ItemsRelinkedCount: 4, Merged: true));

		LinkItemTemplateCommandHandler handler = new(mockService.Object);

		LinkTemplateResult result = await handler.Handle(
			new LinkItemTemplateCommand(descriptionId, templateId),
			CancellationToken.None);

		// The survivor is the template's entry, which is not the id that went in — the handler must
		// forward the result whole rather than echoing the request.
		result.Survivor.Description.Id.Should().Be(survivorId);
		result.ItemsRelinkedCount.Should().Be(4);
		result.Merged.Should().BeTrue();
		mockService.Verify(s => s.LinkTemplateAsync(descriptionId, templateId, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_PropagatesNotFound()
	{
		// Translating this to a null or a false would erase the difference between "the template is
		// gone" and "the link changed nothing", which is the controller's 404-vs-200 decision.
		Mock<INormalizedDescriptionService> mockService = new();
		mockService
			.Setup(s => s.LinkTemplateAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException("Item template not found."));

		LinkItemTemplateCommandHandler handler = new(mockService.Object);

		await handler.Invoking(h => h.Handle(
				new LinkItemTemplateCommand(Guid.NewGuid(), Guid.NewGuid()),
				CancellationToken.None).AsTask())
			.Should().ThrowAsync<KeyNotFoundException>();
	}
}
