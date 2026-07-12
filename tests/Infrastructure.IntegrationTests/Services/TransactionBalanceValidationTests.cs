using Application.Commands.Transaction.Create;
using Application.Commands.Transaction.Update;
using Application.Models;
using Domain;
using Domain.Core;
using FluentAssertions;
using FluentValidation;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Mapping;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

// End-to-end coverage for the database-level balance-validation path (RECEIPTS-763 / RECEIPTS-764)
// against real PostgreSQL. This lives here rather than in the InMemory unit suite because the
// InMemory provider cannot model the SELECT ... FOR UPDATE row lock the fix depends on (and driving
// the fallback path there crashed the test host under coverage on CI).
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TransactionBalanceValidationTests(PostgresFixture fixture)
{
	private static void NoOp(ReceiptBalanceState _) { }

	// The atomic create/update path never touches the repository, but the constructor requires one;
	// wire a real repository against the same factory rather than a mock.
	private TransactionService BuildService()
	{
		IDbContextFactory<ApplicationDbContext> factory = new FixtureContextFactory(fixture);
		return new TransactionService(
			new TransactionRepository(factory),
			new TransactionMapper(),
			new AccountMapper(),
			factory,
			new ReceiptMapper(),
			new ReceiptItemMapper(),
			new AdjustmentMapper());
	}

	// Seeds a receipt (tax $0) with one internally-consistent item (TotalAmount == Quantity *
	// UnitPrice, enforced by the ReceiptItem domain constructor) plus an account and card so a
	// child transaction's required FKs resolve. ExpectedTotal == itemTotal.
	private async Task<(Guid receiptId, Guid accountId, Guid cardId)> SeedReceiptAsync(decimal itemTotal, bool softDelete = false)
	{
		await using ApplicationDbContext setup = fixture.CreateDbContext();

		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.TaxAmount = 0m;

		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;

		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.Quantity = 1m;
		item.UnitPrice = itemTotal;
		item.TotalAmount = itemTotal;

		setup.Receipts.Add(receipt);
		setup.Accounts.Add(account);
		setup.Cards.Add(card);
		setup.ReceiptItems.Add(item);
		await setup.SaveChangesAsync();

		if (softDelete)
		{
			setup.Receipts.Remove(receipt); // intercepted as a soft delete; cascades to the tracked item
			await setup.SaveChangesAsync();
		}

		return (receipt.Id, account.Id, card.Id);
	}

	private async Task<int> CountTransactionsAsync(Guid receiptId)
	{
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		return await verify.Transactions.IgnoreQueryFilters().IgnoreAutoIncludes().CountAsync(t => t.ReceiptId == receiptId);
	}

	private static Transaction Tx(Guid cardId, decimal amount, Guid accountId, Guid? id = null) =>
		new(id ?? Guid.NewGuid(), cardId, new Money(amount), DateOnly.FromDateTime(DateTime.Now)) { AccountId = accountId };

	[Fact]
	public async Task Create_MissingReceipt_ThrowsKeyNotFound()
	{
		// Arrange — no receipt seeded (RECEIPTS-763).
		Guid receiptId = Guid.NewGuid();
		List<Transaction> input = [Tx(Guid.NewGuid(), 50m, Guid.NewGuid())];

		// Act
		Func<Task> act = async () => await BuildService().CreateWithBalanceValidationAsync(input, receiptId, NoOp, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<KeyNotFoundException>();
		(await CountTransactionsAsync(receiptId)).Should().Be(0);
	}

	[Fact]
	public async Task Create_SoftDeletedReceipt_ThrowsKeyNotFoundAndCreatesNoOrphan()
	{
		// Arrange — the FK row still exists but the receipt is soft-deleted; the FOR UPDATE lookup
		// filters on DeletedAt IS NULL, so it reads as absent and no active transaction is orphaned.
		(Guid receiptId, _, _) = await SeedReceiptAsync(itemTotal: 50m, softDelete: true);
		List<Transaction> input = [Tx(Guid.NewGuid(), 50m, Guid.NewGuid())];

		// Act
		Func<Task> act = async () => await BuildService().CreateWithBalanceValidationAsync(input, receiptId, NoOp, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<KeyNotFoundException>();
		(await CountTransactionsAsync(receiptId)).Should().Be(0);
	}

	[Fact]
	public async Task Create_Balanced_PersistsTransaction()
	{
		// Arrange — ExpectedTotal $50; a single $50 transaction balances.
		(Guid receiptId, Guid accountId, Guid cardId) = await SeedReceiptAsync(itemTotal: 50m);
		List<Transaction> input = [Tx(cardId, 50m, accountId)];

		// Act
		List<Transaction> result = await BuildService().CreateWithBalanceValidationAsync(input, receiptId, NoOp, CancellationToken.None);

		// Assert
		result.Should().ContainSingle();
		(await CountTransactionsAsync(receiptId)).Should().Be(1);
	}

	[Fact]
	public async Task Create_ValidatorRejects_PersistsNothing()
	{
		// Arrange
		(Guid receiptId, Guid accountId, Guid cardId) = await SeedReceiptAsync(itemTotal: 50m);
		List<Transaction> input = [Tx(cardId, 999m, accountId)];

		// Act — validator rejects (mirrors the handler's balance-equation failure).
		Func<Task> act = async () => await BuildService().CreateWithBalanceValidationAsync(
			input, receiptId, _ => throw new ValidationException("unbalanced"), CancellationToken.None);

		// Assert — the insert must roll back with the transaction.
		await act.Should().ThrowAsync<ValidationException>();
		(await CountTransactionsAsync(receiptId)).Should().Be(0);
	}

	[Fact]
	public async Task Update_ExistingTransaction_ChangesAmountInPlace()
	{
		// Arrange — one balanced $50 transaction on the receipt.
		(Guid receiptId, Guid accountId, Guid cardId) = await SeedReceiptAsync(itemTotal: 50m);
		Guid txId = Guid.NewGuid();
		{
			await using ApplicationDbContext setup = fixture.CreateDbContext();
			setup.Transactions.Add(new TransactionEntity
			{
				Id = txId,
				ReceiptId = receiptId,
				AccountId = accountId,
				CardId = cardId,
				Amount = 50m,
				AmountCurrency = Common.Currency.USD,
				Date = DateOnly.FromDateTime(DateTime.Now)
			});
			await setup.SaveChangesAsync();
		}

		// Act — NoOp validation isolates the write path.
		await BuildService().UpdateWithBalanceValidationAsync([Tx(cardId, 42m, accountId, txId)], receiptId, NoOp, CancellationToken.None);

		// Assert
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		TransactionEntity persisted = await verify.Transactions.IgnoreAutoIncludes().SingleAsync(t => t.Id == txId);
		persisted.Amount.Should().Be(42m);
	}

	[Fact]
	public async Task ConcurrentCreates_ForSameReceipt_SerializeSoOnlyOneSucceeds()
	{
		// Proves the RECEIPTS-764 per-receipt row lock holds the invariant under real concurrency:
		// two $50 creates race for a $50 receipt; the FOR UPDATE lock serializes them, so the loser
		// sees the winner's committed write and is rejected by the balance equation. Exactly one wins.
		(Guid receiptId, Guid accountId, Guid cardId) = await SeedReceiptAsync(itemTotal: 50m);

		CreateTransactionCommandHandler handler = new(BuildService());
		CreateTransactionCommand Command() => new([Tx(cardId, 50m, accountId)], receiptId);

		// Both requests are in flight before either awaits, so they contend on the row lock.
		Task<Exception?> first = CaptureAsync(handler.Handle(Command(), CancellationToken.None).AsTask());
		Task<Exception?> second = CaptureAsync(handler.Handle(Command(), CancellationToken.None).AsTask());
		Exception?[] outcomes = await Task.WhenAll(first, second);

		outcomes.Count(e => e is null).Should().Be(1, "exactly one concurrent create may satisfy the $50 balance");
		outcomes.Count(e => e is ValidationException).Should().Be(1, "the losing create must be rejected by the balance equation, not a server error");

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		List<TransactionEntity> persisted = await verify.Transactions
			.IgnoreAutoIncludes()
			.Where(t => t.ReceiptId == receiptId)
			.ToListAsync();

		persisted.Should().ContainSingle("the receipt must never be over-allocated");
		persisted.Sum(t => t.Amount).Should().Be(50m);
	}

	[Fact]
	public async Task ConcurrentUpdates_ForSameReceipt_SerializeSoOnlyOneSucceeds()
	{
		// RECEIPTS-805: the per-receipt FOR UPDATE lock must serialize the UPDATE balance-validation
		// path exactly as it does the CREATE path above. Seed a $50 receipt with two $10 transactions
		// (currently unbalanced at $20). Two concurrent updates each raise a DIFFERENT transaction to
		// $40: against the pre-state each looks balanced ($40 + the other's stale $10 == $50), but
		// applying BOTH would total $80. The row lock forces the loser to re-read the winner's
		// committed write and be rejected by the balance equation. Without the lock, both would read
		// the stale $10 pre-state and commit, over-allocating the receipt — so this asserts the lock,
		// not just the happy path. (Amounts are non-zero because the Money domain type forbids zero.)
		(Guid receiptId, Guid accountId, Guid cardId) = await SeedReceiptAsync(itemTotal: 50m);

		Guid tx1Id = Guid.NewGuid();
		Guid tx2Id = Guid.NewGuid();
		await SeedTransactionsAsync(receiptId, accountId, cardId, amount: 10m, tx1Id, tx2Id);

		UpdateTransactionCommandHandler handler = new(BuildService());
		UpdateTransactionCommand CommandFor(Guid txId) => new([Tx(cardId, 40m, accountId, txId)]);

		// Both requests are in flight before either commits, so they contend on the receipt row lock.
		Task<Exception?> first = CaptureAsync(handler.Handle(CommandFor(tx1Id), CancellationToken.None).AsTask());
		Task<Exception?> second = CaptureAsync(handler.Handle(CommandFor(tx2Id), CancellationToken.None).AsTask());
		Exception?[] outcomes = await Task.WhenAll(first, second);

		outcomes.Count(e => e is null).Should().Be(1, "exactly one concurrent update may satisfy the $50 balance");
		outcomes.Count(e => e is ValidationException).Should().Be(1, "the losing update must be rejected by the balance equation, not a server error");

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		List<TransactionEntity> persisted = await verify.Transactions
			.IgnoreAutoIncludes()
			.Where(t => t.ReceiptId == receiptId)
			.ToListAsync();

		persisted.Should().HaveCount(2);
		persisted.Sum(t => t.Amount).Should().Be(50m, "the receipt must stay balanced at exactly $50 — never over-allocated to $80");
		persisted.Count(t => t.Amount == 40m).Should().Be(1, "only the winning update may raise its transaction to $40");
		persisted.Count(t => t.Amount == 10m).Should().Be(1, "the loser's transaction keeps its pre-update $10 value");
	}

	private async Task SeedTransactionsAsync(Guid receiptId, Guid accountId, Guid cardId, decimal amount, Guid tx1Id, Guid tx2Id)
	{
		await using ApplicationDbContext setup = fixture.CreateDbContext();
		setup.Transactions.AddRange(
			SeedTransaction(tx1Id, receiptId, accountId, cardId, amount),
			SeedTransaction(tx2Id, receiptId, accountId, cardId, amount));
		await setup.SaveChangesAsync();

		static TransactionEntity SeedTransaction(Guid id, Guid receiptId, Guid accountId, Guid cardId, decimal amount) => new()
		{
			Id = id,
			ReceiptId = receiptId,
			AccountId = accountId,
			CardId = cardId,
			Amount = amount,
			AmountCurrency = Common.Currency.USD,
			Date = DateOnly.FromDateTime(DateTime.Now),
		};
	}

	private static async Task<Exception?> CaptureAsync(Task task)
	{
		try
		{
			await task;
			return null;
		}
		catch (Exception ex)
		{
			return ex;
		}
	}

	private sealed class FixtureContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
