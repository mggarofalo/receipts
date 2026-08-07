using Application.Commands.NormalizedDescription.Split;
using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.NormalizedDescription.Split;

public class SplitNormalizedDescriptionCommandHandlerTests
{
	[Fact]
	public async Task Handle_ForwardsSelectionAndNameAndReturnsCreated()
	{
		// Arrange
		Mock<INormalizedDescriptionService> mockService = new();
		Guid firstId = Guid.NewGuid();
		Guid secondId = Guid.NewGuid();
		NormalizedDescriptionDetail expected = new(
			new Domain.NormalizedDescriptions.NormalizedDescription(
				Guid.NewGuid(),
				"cherry cola",
				NormalizedDescriptionStatus.Active,
				new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero)),
			LinkedItemCount: 1,
			NearestNeighbourName: null,
			["cherry cola"]);

		mockService
			.Setup(s => s.SplitAsync(
				It.IsAny<IReadOnlyList<Guid>>(),
				It.IsAny<string>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		SplitNormalizedDescriptionCommandHandler handler = new(mockService.Object);
		SplitNormalizedDescriptionCommand command = new([firstId, secondId], "cherry cola");

		// Act
		NormalizedDescriptionDetail actual = await handler.Handle(command, CancellationToken.None);

		// Assert
		actual.Should().BeSameAs(expected);
		// The whole selection and the caller's name both reach the service — dropping either
		// would silently turn a multi-item split into a single-item one, or rename the result.
		mockService.Verify(
			s => s.SplitAsync(
				It.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { firstId, secondId })),
				"cherry cola",
				It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task Handle_ServicePropagatesKeyNotFound()
	{
		// The service throws KeyNotFoundException when the receipt item is missing; the
		// handler should propagate rather than swallow so controller/test callers can map
		// it to a 404.
		Mock<INormalizedDescriptionService> mockService = new();
		Guid missing = Guid.NewGuid();

		mockService
			.Setup(s => s.SplitAsync(
				It.IsAny<IReadOnlyList<Guid>>(),
				It.IsAny<string>(),
				It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException("Receipt item not found."));

		SplitNormalizedDescriptionCommandHandler handler = new(mockService.Object);
		SplitNormalizedDescriptionCommand command = new([missing], "cherry cola");

		Func<Task> act = async () => await handler.Handle(command, CancellationToken.None);
		await act.Should().ThrowAsync<KeyNotFoundException>();
	}
}
