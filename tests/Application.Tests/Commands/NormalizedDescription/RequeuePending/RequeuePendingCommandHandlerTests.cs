using Application.Commands.NormalizedDescription.RequeuePending;
using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.NormalizedDescription.RequeuePending;

public class RequeuePendingCommandHandlerTests
{
	[Fact]
	public async Task Handle_ItemsUnlinked_SignalsTheResolver()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		Mock<IDescriptionChangeSignal> mockSignal = new();

		mockService
			.Setup(s => s.RequeuePendingAsync(3, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RequeuePendingResult(3, 12, 9));

		RequeuePendingCommandHandler handler = new(mockService.Object, mockSignal.Object);

		RequeuePendingResult? result = await handler.Handle(new RequeuePendingCommand(3), CancellationToken.None);

		result.Should().NotBeNull();
		result!.DeletedDescriptionCount.Should().Be(3);
		result.UnlinkedItemCount.Should().Be(12);
		result.ClearedMatchScoreCount.Should().Be(9);
		// Without the wake-up the requeued items sit idle for up to a full poll interval.
		mockSignal.Verify(s => s.NotifyDirty(), Times.Once);
	}

	[Fact]
	public async Task Handle_CountMismatch_ReturnsNullAndDoesNotSignal()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		Mock<IDescriptionChangeSignal> mockSignal = new();

		mockService
			.Setup(s => s.RequeuePendingAsync(5, It.IsAny<CancellationToken>()))
			.ReturnsAsync((RequeuePendingResult?)null);

		RequeuePendingCommandHandler handler = new(mockService.Object, mockSignal.Object);

		RequeuePendingResult? result = await handler.Handle(new RequeuePendingCommand(5), CancellationToken.None);

		// Nothing was deleted, so there is nothing for the resolver to pick up. Waking it would
		// burn a cycle scanning a set that did not change.
		result.Should().BeNull();
		mockSignal.Verify(s => s.NotifyDirty(), Times.Never);
	}

	[Fact]
	public async Task Handle_NothingPending_DoesNotSignal()
	{
		Mock<INormalizedDescriptionService> mockService = new();
		Mock<IDescriptionChangeSignal> mockSignal = new();

		mockService
			.Setup(s => s.RequeuePendingAsync(0, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RequeuePendingResult(0, 0, 0));

		RequeuePendingCommandHandler handler = new(mockService.Object, mockSignal.Object);

		RequeuePendingResult? result = await handler.Handle(new RequeuePendingCommand(0), CancellationToken.None);

		result!.DeletedDescriptionCount.Should().Be(0);
		mockSignal.Verify(s => s.NotifyDirty(), Times.Never);
	}
}
