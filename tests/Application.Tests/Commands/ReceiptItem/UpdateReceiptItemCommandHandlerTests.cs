using Application.Commands.ReceiptItem.Update;
using Application.Interfaces.Services;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Commands.ReceiptItem;

public class UpdateReceiptItemCommandHandlerTests
{
	[Fact]
	public async Task UpdateReceiptItemCommandHandler_WithValidCommand_ReturnsTrueAndCallsUpdateAndSaveChanges()
	{
		Mock<IReceiptItemService> mockService = new();
		UpdateReceiptItemCommandHandler handler = new(mockService.Object);

		List<Domain.Core.ReceiptItem> input = ReceiptItemGenerator.GenerateList(2);
		Guid receiptId = Guid.NewGuid();

		// The handler calls GetByIdAsync to look up the receiptId from the existing item
		Domain.Core.ReceiptItem existingItem = ReceiptItemGenerator.Generate();
		mockService.Setup(r => r.GetByIdAsync(input[0].Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(existingItem);

		mockService.Setup(r => r
			.UpdateAsync(It.IsAny<List<Domain.Core.ReceiptItem>>(), existingItem.ReceiptId, It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		UpdateReceiptItemCommand command = new(input);
		bool result = await handler.Handle(command, CancellationToken.None);

		Assert.True(result);
	}

	[Fact]
	public async Task UpdateReceiptItemCommandHandler_WithMissingItem_ReturnsFalseAndNeverPersists()
	{
		Mock<IReceiptItemService> mockService = new();
		UpdateReceiptItemCommandHandler handler = new(mockService.Object);

		List<Domain.Core.ReceiptItem> input = ReceiptItemGenerator.GenerateList(1);

		// A soft-deleted/missing target yields null from GetByIdAsync → 404 (RECEIPTS-761).
		mockService.Setup(r => r.GetByIdAsync(input[0].Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync((Domain.Core.ReceiptItem?)null);

		UpdateReceiptItemCommand command = new(input);
		bool result = await handler.Handle(command, CancellationToken.None);

		Assert.False(result);
		mockService.Verify(r => r.UpdateAsync(It.IsAny<List<Domain.Core.ReceiptItem>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}
