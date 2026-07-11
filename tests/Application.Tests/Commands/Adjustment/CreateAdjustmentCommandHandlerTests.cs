using Application.Commands.Adjustment.Create;
using Application.Interfaces.Services;
using FluentAssertions;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Commands.Adjustment;

public class CreateAdjustmentCommandHandlerTests
{
	private readonly Mock<IAdjustmentService> _adjustmentService = new();
	private readonly Mock<IReceiptService> _receiptService = new();

	private CreateAdjustmentCommandHandler CreateHandler() =>
		new(_adjustmentService.Object, _receiptService.Object);

	[Fact]
	public async Task Handle_WithExistingReceipt_ReturnsCreatedAdjustments()
	{
		Guid receiptId = Guid.NewGuid();
		List<Domain.Core.Adjustment> input = AdjustmentGenerator.GenerateList(2);

		_receiptService.Setup(r => r.ExistsAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
		_adjustmentService.Setup(a => a
			.CreateAsync(It.IsAny<List<Domain.Core.Adjustment>>(), receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(input);

		CreateAdjustmentCommand command = new(input, receiptId);
		List<Domain.Core.Adjustment> result = await CreateHandler().Handle(command, CancellationToken.None);

		result.Should().HaveCount(input.Count);
	}

	[Fact]
	public async Task Handle_MissingReceipt_ThrowsKeyNotFoundExceptionAndNeverCreates()
	{
		// RECEIPTS-763: a nonexistent receipt must 404 instead of surfacing an FK-violation 500.
		Guid receiptId = Guid.NewGuid();
		_receiptService.Setup(r => r.ExistsAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

		CreateAdjustmentCommand command = new(AdjustmentGenerator.GenerateList(1), receiptId);

		Func<Task> act = async () => await CreateHandler().Handle(command, CancellationToken.None);

		await act.Should().ThrowAsync<KeyNotFoundException>();
		_adjustmentService.Verify(a => a.CreateAsync(
			It.IsAny<List<Domain.Core.Adjustment>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_SoftDeletedReceipt_ThrowsKeyNotFoundExceptionAndNeverCreates()
	{
		// RECEIPTS-763: ExistsAsync respects the soft-delete query filter, so a trashed receipt
		// reads as absent — closing the create-under-trashed-receipt orphan hole (previously the
		// adjustment path did NO receipt check and created an active adjustment under a 404 receipt).
		Guid receiptId = Guid.NewGuid();
		_receiptService.Setup(r => r.ExistsAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

		CreateAdjustmentCommand command = new(AdjustmentGenerator.GenerateList(1), receiptId);

		Func<Task> act = async () => await CreateHandler().Handle(command, CancellationToken.None);

		await act.Should().ThrowAsync<KeyNotFoundException>();
		_adjustmentService.Verify(a => a.CreateAsync(
			It.IsAny<List<Domain.Core.Adjustment>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}
