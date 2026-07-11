using Application.Commands.Transaction.Update;
using Application.Interfaces.Services;
using Application.Models;
using Common;
using Domain;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace Application.Tests.Commands.Transaction.Update;

public class UpdateTransactionCommandHandlerTests
{
	private readonly Mock<ITransactionService> _transactionService = new();
	private readonly Mock<IReceiptService> _receiptService = new();
	private readonly Mock<IReceiptItemService> _receiptItemService = new();
	private readonly Mock<IAdjustmentService> _adjustmentService = new();

	private UpdateTransactionCommandHandler CreateHandler() =>
		new(_transactionService.Object, _receiptService.Object,
			_receiptItemService.Object, _adjustmentService.Object);

	private void SetupReceiptData(Guid receiptId, Guid firstTxId, List<Domain.Core.Transaction> existingTransactions)
	{
		// Receipt: TaxAmount = $10
		Domain.Core.Receipt receipt = new(Guid.NewGuid(), "Test", DateOnly.FromDateTime(DateTime.Now), new Money(10));

		// Item: qty=1, unitPrice=$5, totalAmount=$5 → Subtotal = $5
		Domain.Core.ReceiptItem item = new(Guid.NewGuid(), "CODE", "Item", 1, new Money(5), new Money(5), "Cat", "Sub");

		// ExpectedTotal = Subtotal($5) + TaxAmount($10) + Adjustments($0) = $15

		// The handler calls GetByIdAsync to look up the receiptId from the first transaction
		Domain.Core.Transaction existingForLookup = new(firstTxId, Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now)) { ReceiptId = receiptId };
		_transactionService.Setup(s => s.GetByIdAsync(firstTxId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(existingForLookup);

		_receiptService.Setup(s => s.GetByIdAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(receipt);
		_receiptItemService.Setup(s => s.GetByReceiptIdAsync(receiptId, 0, int.MaxValue, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.ReceiptItem>([item], 1, 0, int.MaxValue));
		_adjustmentService.Setup(s => s.GetByReceiptIdAsync(receiptId, 0, int.MaxValue, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.Adjustment>([], 0, 0, int.MaxValue));
		_transactionService.Setup(s => s.GetByReceiptIdAsync(receiptId, 0, int.MaxValue, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.Transaction>(existingTransactions, existingTransactions.Count, 0, int.MaxValue));
	}

	[Fact]
	public async Task Handle_SingleGroupBalanced_ReturnsTrue()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();
		Guid txId = Guid.NewGuid();
		Domain.Core.Transaction existing = new(txId, Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now));
		SetupReceiptData(receiptId, txId, [existing]);

		List<Domain.Core.Transaction> updated = [new(txId, Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now)) { AccountId = accountId }];

		_transactionService.Setup(s => s.UpdateAsync(
				It.IsAny<List<Domain.Core.Transaction>>(), receiptId, It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		UpdateTransactionCommandHandler handler = CreateHandler();
		UpdateTransactionCommand command = new(updated);

		// Act
		bool result = await handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().BeTrue();
	}

	[Fact]
	public async Task Handle_MultipleGroupsBalanced_ReturnsTrue()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Guid accountId1 = Guid.NewGuid();
		Guid accountId2 = Guid.NewGuid();
		Guid txId1 = Guid.NewGuid();
		Guid txId2 = Guid.NewGuid();

		Domain.Core.Transaction existing1 = new(txId1, Guid.NewGuid(), new Money(10), DateOnly.FromDateTime(DateTime.Now));
		Domain.Core.Transaction existing2 = new(txId2, Guid.NewGuid(), new Money(5), DateOnly.FromDateTime(DateTime.Now));
		SetupReceiptData(receiptId, txId1, [existing1, existing2]);

		List<Domain.Core.Transaction> updated =
		[
			new(txId1, Guid.NewGuid(), new Money(10), DateOnly.FromDateTime(DateTime.Now)) { AccountId = accountId1 },
			new(txId2, Guid.NewGuid(), new Money(5), DateOnly.FromDateTime(DateTime.Now)) { AccountId = accountId2 }
		];

		_transactionService.Setup(s => s.UpdateAsync(
				It.IsAny<List<Domain.Core.Transaction>>(), receiptId, It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		UpdateTransactionCommandHandler handler = CreateHandler();
		UpdateTransactionCommand command = new(updated);

		// Act
		bool result = await handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().BeTrue();
		_transactionService.Verify(s => s.UpdateAsync(
			It.IsAny<List<Domain.Core.Transaction>>(), receiptId, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_UnbalancedTransactions_ThrowsValidationExceptionAndNeverPersists()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Guid accountId1 = Guid.NewGuid();
		Guid accountId2 = Guid.NewGuid();
		Guid txId1 = Guid.NewGuid();
		Guid txId2 = Guid.NewGuid();

		Domain.Core.Transaction existing1 = new(txId1, Guid.NewGuid(), new Money(10), DateOnly.FromDateTime(DateTime.Now));
		Domain.Core.Transaction existing2 = new(txId2, Guid.NewGuid(), new Money(5), DateOnly.FromDateTime(DateTime.Now));
		SetupReceiptData(receiptId, txId1, [existing1, existing2]);

		// Update to unbalanced totals: $100 + $50 = $150 ≠ $15
		List<Domain.Core.Transaction> updated =
		[
			new(txId1, Guid.NewGuid(), new Money(100), DateOnly.FromDateTime(DateTime.Now)) { AccountId = accountId1 },
			new(txId2, Guid.NewGuid(), new Money(50), DateOnly.FromDateTime(DateTime.Now)) { AccountId = accountId2 }
		];

		UpdateTransactionCommandHandler handler = CreateHandler();
		UpdateTransactionCommand command = new(updated);

		// Act
		Func<Task> act = async () => await handler.Handle(command, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<ValidationException>();
		_transactionService.Verify(s => s.UpdateAsync(
			It.IsAny<List<Domain.Core.Transaction>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_ReceiptNotFound_ThrowsInvalidOperationException()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Guid txId = Guid.NewGuid();

		Domain.Core.Transaction existingForLookup = new(txId, Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now)) { ReceiptId = receiptId };
		_transactionService.Setup(s => s.GetByIdAsync(txId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(existingForLookup);

		_receiptService.Setup(s => s.GetByIdAsync(receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((Domain.Core.Receipt?)null);
		_receiptItemService.Setup(s => s.GetByReceiptIdAsync(receiptId, 0, int.MaxValue, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.ReceiptItem>([], 0, 0, int.MaxValue));
		_adjustmentService.Setup(s => s.GetByReceiptIdAsync(receiptId, 0, int.MaxValue, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.Adjustment>([], 0, 0, int.MaxValue));
		_transactionService.Setup(s => s.GetByReceiptIdAsync(receiptId, 0, int.MaxValue, It.IsAny<SortParams>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PagedResult<Domain.Core.Transaction>([], 0, 0, int.MaxValue));

		List<Domain.Core.Transaction> updated = [new(txId, Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }];

		UpdateTransactionCommandHandler handler = CreateHandler();
		UpdateTransactionCommand command = new(updated);

		// Act
		Func<Task> act = async () => await handler.Handle(command, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task Handle_TransactionNotFound_ReturnsFalseAndNeverPersists()
	{
		// Arrange — the update target is missing/soft-deleted, so the endpoint must 404
		// (handler returns false) rather than 500 (RECEIPTS-761).
		Guid txId = Guid.NewGuid();
		_transactionService.Setup(s => s.GetByIdAsync(txId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((Domain.Core.Transaction?)null);

		List<Domain.Core.Transaction> updated = [new(txId, Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }];

		UpdateTransactionCommandHandler handler = CreateHandler();
		UpdateTransactionCommand command = new(updated);

		// Act
		bool result = await handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().BeFalse();
		_transactionService.Verify(s => s.UpdateAsync(
			It.IsAny<List<Domain.Core.Transaction>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_TransactionFromDifferentReceipt_ThrowsInvalidOperationException()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Guid txId = Guid.NewGuid();
		Guid foreignTxId = Guid.NewGuid();

		// existing transaction belongs to receiptId
		Domain.Core.Transaction existing = new(txId, Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now));
		SetupReceiptData(receiptId, txId, [existing]);

		// batch includes a transaction ID that doesn't exist in the receipt's transaction list
		List<Domain.Core.Transaction> updated =
		[
			new(txId, Guid.NewGuid(), new Money(10), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() },
			new(foreignTxId, Guid.NewGuid(), new Money(5), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }
		];

		UpdateTransactionCommandHandler handler = CreateHandler();
		UpdateTransactionCommand command = new(updated);

		// Act
		Func<Task> act = async () => await handler.Handle(command, CancellationToken.None);

		// Assert
		InvalidOperationException exception = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
		exception.Message.Should().Be("All transactions in the batch must belong to the same receipt.");
	}
}
