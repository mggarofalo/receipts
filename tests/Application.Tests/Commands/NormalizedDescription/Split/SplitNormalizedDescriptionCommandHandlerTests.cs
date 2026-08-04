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
	public async Task Handle_ForwardsReceiptItemIdAndReturnsCreated()
	{
		// Arrange
		Mock<INormalizedDescriptionService> mockService = new();
		Guid receiptItemId = Guid.NewGuid();
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
			.Setup(s => s.SplitAsync(receiptItemId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expected);

		SplitNormalizedDescriptionCommandHandler handler = new(mockService.Object);
		SplitNormalizedDescriptionCommand command = new(receiptItemId);

		// Act
		NormalizedDescriptionDetail actual = await handler.Handle(command, CancellationToken.None);

		// Assert
		actual.Should().BeSameAs(expected);
		mockService.Verify(s => s.SplitAsync(receiptItemId, It.IsAny<CancellationToken>()), Times.Once);
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
			.Setup(s => s.SplitAsync(missing, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException("Receipt item not found."));

		SplitNormalizedDescriptionCommandHandler handler = new(mockService.Object);
		SplitNormalizedDescriptionCommand command = new(missing);

		Func<Task> act = async () => await handler.Handle(command, CancellationToken.None);
		await act.Should().ThrowAsync<KeyNotFoundException>();
	}
}
