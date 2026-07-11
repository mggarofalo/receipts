using Application.Commands.Transaction.Create;
using Application.Interfaces.Services;
using Application.Models;
using Common;
using Domain;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace Application.Tests.Commands.Transaction.Create;

public class CreateTransactionCommandHandlerTests
{
	private readonly Mock<ITransactionService> _transactionService = new();

	private CreateTransactionCommandHandler CreateHandler() => new(_transactionService.Object);

	// ExpectedTotal = Subtotal($5) + TaxAmount($10) + Adjustments($0) = $15
	private static ReceiptBalanceState BuildState(Guid receiptId, List<Domain.Core.Transaction>? existing = null)
	{
		Domain.Core.Receipt receipt = new(receiptId, "Test", DateOnly.FromDateTime(DateTime.Now), new Money(10));
		Domain.Core.ReceiptItem item = new(Guid.NewGuid(), "CODE", "Item", 1, new Money(5), new Money(5), "Cat", "Sub");

		return new ReceiptBalanceState
		{
			Receipt = receipt,
			Items = [item],
			Adjustments = [],
			ExistingTransactions = existing ?? []
		};
	}

	// The real service runs the caller's validation delegate INSIDE its row-locked transaction
	// (RECEIPTS-764). Emulate that here so these tests exercise the handler's real balance logic.
	private void SetupCreateRunsValidation(Guid receiptId, ReceiptBalanceState state)
	{
		_transactionService
			.Setup(s => s.CreateWithBalanceValidationAsync(
				It.IsAny<List<Domain.Core.Transaction>>(), receiptId,
				It.IsAny<Action<ReceiptBalanceState>>(), It.IsAny<CancellationToken>()))
			.Returns((List<Domain.Core.Transaction> models, Guid _, Action<ReceiptBalanceState> validate, CancellationToken _) =>
			{
				validate(state);
				return Task.FromResult(models);
			});
	}

	[Fact]
	public async Task Handle_SingleGroupBalanced_ReturnsCreatedTransactions()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		List<Domain.Core.Transaction> input =
		[
			new(Guid.NewGuid(), Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }
		];
		SetupCreateRunsValidation(receiptId, BuildState(receiptId));

		CreateTransactionCommandHandler handler = CreateHandler();
		CreateTransactionCommand command = new(input, receiptId);

		// Act
		List<Domain.Core.Transaction> result = await handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().HaveCount(1);
		result[0].Amount.Amount.Should().Be(15);
	}

	[Fact]
	public async Task Handle_MultipleGroupsBalanced_ReturnsAllCreatedTransactions()
	{
		// Arrange
		Guid receiptId = Guid.NewGuid();
		Domain.Core.Transaction tx1 = new(Guid.NewGuid(), Guid.NewGuid(), new Money(10), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() };
		Domain.Core.Transaction tx2 = new(Guid.NewGuid(), Guid.NewGuid(), new Money(5), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() };
		List<Domain.Core.Transaction> input = [tx1, tx2];

		SetupCreateRunsValidation(receiptId, BuildState(receiptId));

		CreateTransactionCommandHandler handler = CreateHandler();
		CreateTransactionCommand command = new(input, receiptId);

		// Act
		List<Domain.Core.Transaction> result = await handler.Handle(command, CancellationToken.None);

		// Assert
		result.Should().HaveCount(2);
		result.Sum(t => t.Amount.Amount).Should().Be(15);
		_transactionService.Verify(s => s.CreateWithBalanceValidationAsync(
			It.IsAny<List<Domain.Core.Transaction>>(), receiptId,
			It.IsAny<Action<ReceiptBalanceState>>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_UnbalancedTransactions_ThrowsValidationException()
	{
		// Arrange — total $100 + $50 = $150 ≠ ExpectedTotal of $15
		Guid receiptId = Guid.NewGuid();
		List<Domain.Core.Transaction> input =
		[
			new(Guid.NewGuid(), Guid.NewGuid(), new Money(100), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() },
			new(Guid.NewGuid(), Guid.NewGuid(), new Money(50), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }
		];
		SetupCreateRunsValidation(receiptId, BuildState(receiptId));

		CreateTransactionCommandHandler handler = CreateHandler();
		CreateTransactionCommand command = new(input, receiptId);

		// Act
		Func<Task> act = async () => await handler.Handle(command, CancellationToken.None);

		// Assert — the validation delegate runs inside the service's serialized transaction and rejects.
		await act.Should().ThrowAsync<ValidationException>();
	}

	[Fact]
	public async Task Handle_MissingReceipt_PropagatesKeyNotFoundException()
	{
		// Arrange — the service's row-lock finds no active receipt row and throws (→ 404). RECEIPTS-763.
		Guid receiptId = Guid.NewGuid();
		_transactionService
			.Setup(s => s.CreateWithBalanceValidationAsync(
				It.IsAny<List<Domain.Core.Transaction>>(), receiptId,
				It.IsAny<Action<ReceiptBalanceState>>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException($"Receipt {receiptId} not found."));

		List<Domain.Core.Transaction> input =
		[
			new(Guid.NewGuid(), Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }
		];

		CreateTransactionCommandHandler handler = CreateHandler();
		CreateTransactionCommand command = new(input, receiptId);

		// Act
		Func<Task> act = async () => await handler.Handle(command, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<KeyNotFoundException>();
	}

	[Fact]
	public async Task Handle_SoftDeletedReceipt_PropagatesKeyNotFoundException()
	{
		// Arrange — a soft-deleted receipt is indistinguishable from missing at the service
		// boundary (the row-lock query filters on DeletedAt IS NULL), so it also 404s with no
		// orphan created. The real missing-vs-soft-deleted distinction is proven in
		// Infrastructure.Tests against the live query filter. RECEIPTS-763.
		Guid receiptId = Guid.NewGuid();
		_transactionService
			.Setup(s => s.CreateWithBalanceValidationAsync(
				It.IsAny<List<Domain.Core.Transaction>>(), receiptId,
				It.IsAny<Action<ReceiptBalanceState>>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new KeyNotFoundException($"Receipt {receiptId} not found."));

		List<Domain.Core.Transaction> input =
		[
			new(Guid.NewGuid(), Guid.NewGuid(), new Money(15), DateOnly.FromDateTime(DateTime.Now)) { AccountId = Guid.NewGuid() }
		];

		CreateTransactionCommandHandler handler = CreateHandler();
		CreateTransactionCommand command = new(input, receiptId);

		// Act
		Func<Task> act = async () => await handler.Handle(command, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<KeyNotFoundException>();
	}
}
