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

	private UpdateTransactionCommandHandler CreateHandler() => new(_transactionService.Object);

	// ExpectedTotal = Subtotal($5) + TaxAmount($10) + Adjustments($0) = $15
	private static ReceiptBalanceState BuildState(Guid receiptId, List<Domain.Core.Transaction> existingTransactions)
	{
		Domain.Core.Receipt receipt = new(receiptId, "Test", DateOnly.FromDateTime(DateTime.Now), new Money(10));
		Domain.Core.ReceiptItem item = new(Guid.NewGuid(), "CODE", "Item", 1, new Money(5), new Money(5), "Cat", "Sub");

		return new ReceiptBalanceState
		{
			Receipt = receipt,
			Items = [item],
			Adjustments = [],
			ExistingTransactions = existingTransactions
		};
	}

	// The handler first looks up the receiptId from the first transaction, then delegates the
	// serialized validate-and-write to the service. Emulate the service running the validation
	// delegate against the fresh (row-locked) snapshot. RECEIPTS-764.
	private void SetupLookupAndUpdate(Guid receiptId, Guid firstTxId, ReceiptBalanceState state)
	{
		Domain.Core.Transaction existingForLookup = new(firstTxId, Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now)) { ReceiptId = receiptId };
		_transactionService.Setup(s => s.GetByIdAsync(firstTxId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(existingForLookup);

		_transactionService
			.Setup(s => s.UpdateWithBalanceValidationAsync(
				It.IsAny<List<Domain.Core.Transaction>>(), receiptId,
				It.IsAny<Action<ReceiptBalanceState>>(), It.IsAny<CancellationToken>()))
			.Returns((List<Domain.Core.Transaction> _, Guid _, Action<ReceiptBalanceState> validate, CancellationToken _) =>
			{
				validate(state);
				return Task.CompletedTask;
			});
	}

	[Fact]
	public async Task Handle_SingleGroupBalanced_ReturnsTrue()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Guid txId = Guid.NewGuid();
		Domain.Core.Transaction existing = new(txId, Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now));
		SetupLookupAndUpdate(receiptId, txId, BuildState(receiptId, [existing]));

		List<Domain.Core.Transaction> updated = [new(txId, Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }];

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
		Guid txId1 = Guid.NewGuid();
		Guid txId2 = Guid.NewGuid();

		Domain.Core.Transaction existing1 = new(txId1, Guid.NewGuid(), new Money(10), DateOnly.FromDateTime(DateTime.Now));
		Domain.Core.Transaction existing2 = new(txId2, Guid.NewGuid(), new Money(5), DateOnly.FromDateTime(DateTime.Now));
		SetupLookupAndUpdate(receiptId, txId1, BuildState(receiptId, [existing1, existing2]));

		List<Domain.Core.Transaction> updated =
		[
			new(txId1, Guid.NewGuid(), new Money(10), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() },
			new(txId2, Guid.NewGuid(), new Money(5), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }
		];

		UpdateTransactionCommandHandler handler = CreateHandler();
		UpdateTransactionCommand command = new(updated);

		// Act
		bool result = await handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().BeTrue();
		_transactionService.Verify(s => s.UpdateWithBalanceValidationAsync(
			It.IsAny<List<Domain.Core.Transaction>>(), receiptId,
			It.IsAny<Action<ReceiptBalanceState>>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_UnbalancedTransactions_ThrowsValidationException()
	{
		// Arrange — update to unbalanced totals: $100 + $50 = $150 ≠ $15
		Guid receiptId = Guid.NewGuid();
		Guid txId1 = Guid.NewGuid();
		Guid txId2 = Guid.NewGuid();

		Domain.Core.Transaction existing1 = new(txId1, Guid.NewGuid(), new Money(10), DateOnly.FromDateTime(DateTime.Now));
		Domain.Core.Transaction existing2 = new(txId2, Guid.NewGuid(), new Money(5), DateOnly.FromDateTime(DateTime.Now));
		SetupLookupAndUpdate(receiptId, txId1, BuildState(receiptId, [existing1, existing2]));

		List<Domain.Core.Transaction> updated =
		[
			new(txId1, Guid.NewGuid(), new Money(100), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() },
			new(txId2, Guid.NewGuid(), new Money(50), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }
		];

		UpdateTransactionCommandHandler handler = CreateHandler();
		UpdateTransactionCommand command = new(updated);

		// Act
		Func<Task> act = async () => await handler.Handle(command, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<ValidationException>();
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
		_transactionService.Verify(s => s.UpdateWithBalanceValidationAsync(
			It.IsAny<List<Domain.Core.Transaction>>(), It.IsAny<Guid>(),
			It.IsAny<Action<ReceiptBalanceState>>(), It.IsAny<CancellationToken>()), Times.Never);
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
		SetupLookupAndUpdate(receiptId, txId, BuildState(receiptId, [existing]));

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
