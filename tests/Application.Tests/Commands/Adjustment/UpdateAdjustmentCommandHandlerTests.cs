using Application.Commands.Adjustment.Update;
using Application.Interfaces.Services;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Commands.Adjustment;

public class UpdateAdjustmentCommandHandlerTests
{
	[Fact]
	public async Task UpdateAdjustmentCommandHandler_WithValidCommand_ReturnsTrueAndCallsUpdate()
	{
		Mock<IAdjustmentService> mockService = new();
		UpdateAdjustmentCommandHandler handler = new(mockService.Object);

		List<Domain.Core.Adjustment> input = AdjustmentGenerator.GenerateList(2);

		// The handler calls GetByIdAsync to resolve the ReceiptId from the existing adjustment.
		Domain.Core.Adjustment existing = AdjustmentGenerator.Generate();
		mockService.Setup(r => r.GetByIdAsync(input[0].Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(existing);

		mockService.Setup(r => r
			.UpdateAsync(It.IsAny<List<Domain.Core.Adjustment>>(), existing.ReceiptId, It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		UpdateAdjustmentCommand command = new(input);
		bool result = await handler.Handle(command, CancellationToken.None);

		Assert.True(result);
	}

	[Fact]
	public async Task UpdateAdjustmentCommandHandler_WithMissingAdjustment_ReturnsFalseAndNeverPersists()
	{
		Mock<IAdjustmentService> mockService = new();
		UpdateAdjustmentCommandHandler handler = new(mockService.Object);

		List<Domain.Core.Adjustment> input = AdjustmentGenerator.GenerateList(1);

		// A soft-deleted/missing target yields null from GetByIdAsync → 404 (RECEIPTS-761).
		mockService.Setup(r => r.GetByIdAsync(input[0].Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync((Domain.Core.Adjustment?)null);

		UpdateAdjustmentCommand command = new(input);
		bool result = await handler.Handle(command, CancellationToken.None);

		Assert.False(result);
		mockService.Verify(r => r.UpdateAsync(It.IsAny<List<Domain.Core.Adjustment>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}
