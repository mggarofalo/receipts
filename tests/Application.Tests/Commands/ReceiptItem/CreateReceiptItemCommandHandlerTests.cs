using Application.Commands.ReceiptItem.Create;
using Application.Interfaces.Services;
using FluentAssertions;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Commands.ReceiptItem;

public class CreateReceiptItemCommandHandlerTests
{
	private readonly Mock<IReceiptItemService> _receiptItemService = new();
	private readonly Mock<IReceiptService> _receiptService = new();

	private CreateReceiptItemCommandHandler CreateHandler() =>
		new(_receiptItemService.Object, _receiptService.Object);

	[Fact]
	public async Task CreateReceiptItemCommandHandler_WithValidCommand_ReturnsCreatedReceiptItems()
	{
		Domain.Core.Receipt receipt = ReceiptGenerator.Generate();
		List<Domain.Core.ReceiptItem> input = ReceiptItemGenerator.GenerateList(2);

		_receiptService.Setup(r => r.ExistsAsync(receipt.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
		_receiptItemService.Setup(r => r
			.CreateAsync(It.IsAny<List<Domain.Core.ReceiptItem>>(), receipt.Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(input);

		CreateReceiptItemCommand command = new(input, receipt.Id);
		List<Domain.Core.ReceiptItem> result = await CreateHandler().Handle(command, CancellationToken.None);

		result.Should().HaveCount(input.Count);
	}

	[Fact]
	public async Task Handle_MissingReceipt_ThrowsKeyNotFoundExceptionAndNeverCreates()
	{
		// RECEIPTS-763: a nonexistent receipt must 404 instead of surfacing an FK-violation 500.
		Guid receiptId = Guid.NewGuid();
		_receiptService.Setup(r => r.ExistsAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

		CreateReceiptItemCommand command = new(ReceiptItemGenerator.GenerateList(1), receiptId);

		Func<Task> act = async () => await CreateHandler().Handle(command, CancellationToken.None);

		await act.Should().ThrowAsync<KeyNotFoundException>();
		_receiptItemService.Verify(r => r.CreateAsync(
			It.IsAny<List<Domain.Core.ReceiptItem>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_SoftDeletedReceipt_ThrowsKeyNotFoundExceptionAndNeverCreates()
	{
		// RECEIPTS-763: ExistsAsync respects the soft-delete query filter, so a trashed receipt
		// reads as absent and no active receipt item is orphaned under it.
		Guid receiptId = Guid.NewGuid();
		_receiptService.Setup(r => r.ExistsAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

		CreateReceiptItemCommand command = new(ReceiptItemGenerator.GenerateList(1), receiptId);

		Func<Task> act = async () => await CreateHandler().Handle(command, CancellationToken.None);

		await act.Should().ThrowAsync<KeyNotFoundException>();
		_receiptItemService.Verify(r => r.CreateAsync(
			It.IsAny<List<Domain.Core.ReceiptItem>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}
