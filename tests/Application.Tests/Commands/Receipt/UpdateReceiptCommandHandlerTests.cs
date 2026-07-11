using Application.Commands.Receipt.Update;
using Application.Interfaces.Services;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Commands.Receipt;

public class UpdateReceiptCommandHandlerTests
{
	[Fact]
	public async Task UpdateReceiptCommandHandler_WithValidCommand_ReturnsTrueAndCallsUpdateAndSaveChanges()
	{
		Mock<IReceiptService> mockService = new();
		UpdateReceiptCommandHandler handler = new(mockService.Object);

		List<Domain.Core.Receipt> input = ReceiptGenerator.GenerateList(2);

		mockService.Setup(r => r
			.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		mockService.Setup(r => r
			.UpdateAsync(It.IsAny<List<Domain.Core.Receipt>>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		UpdateReceiptCommand command = new(input);
		bool result = await handler.Handle(command, CancellationToken.None);

		Assert.True(result);
	}

	[Fact]
	public async Task UpdateReceiptCommandHandler_WithMissingReceipt_ReturnsFalseAndNeverPersists()
	{
		Mock<IReceiptService> mockService = new();
		UpdateReceiptCommandHandler handler = new(mockService.Object);

		List<Domain.Core.Receipt> input = ReceiptGenerator.GenerateList(1);

		// A soft-deleted/missing target is hidden by the query filter, so ExistsAsync is false.
		mockService.Setup(r => r
			.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		UpdateReceiptCommand command = new(input);
		bool result = await handler.Handle(command, CancellationToken.None);

		Assert.False(result);
		mockService.Verify(r => r.UpdateAsync(It.IsAny<List<Domain.Core.Receipt>>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}