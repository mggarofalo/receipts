using Application.Commands.Transaction.Create;
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

// Proves that the per-receipt, database-level serialization added for RECEIPTS-764 actually
// holds the balance invariant under real concurrency. Two POST /transactions for the same
// receipt fire at once; the FOR UPDATE row lock forces them to serialize, so the second observes
// the first's committed write and rejects the over-allocation. Exactly one must succeed.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TransactionBalanceConcurrencyTests(PostgresFixture fixture)
{
	// The atomic create path never touches the repository, but the constructor requires one;
	// wire a real repository against the same factory rather than a mock.
	private CreateTransactionCommandHandler BuildHandler()
	{
		IDbContextFactory<ApplicationDbContext> factory = new FixtureContextFactory(fixture);
		TransactionService service = new(
			new TransactionRepository(factory),
			new TransactionMapper(),
			new AccountMapper(),
			factory,
			new ReceiptMapper(),
			new ReceiptItemMapper(),
			new AdjustmentMapper());

		return new CreateTransactionCommandHandler(service);
	}

	[Fact]
	public async Task ConcurrentCreates_ForSameReceipt_SerializeSoOnlyOneSucceeds()
	{
		// Arrange — a receipt whose ExpectedTotal is exactly $50 (subtotal $50, tax $0).
		Guid receiptId;
		Guid accountId;
		Guid cardId;
		{
			await using ApplicationDbContext setup = fixture.CreateDbContext();
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			receipt.TaxAmount = 0m;

			AccountEntity account = AccountEntityGenerator.Generate();
			CardEntity card = CardEntityGenerator.Generate();
			card.AccountId = account.Id;

			// Keep the item internally consistent (TotalAmount == Quantity * UnitPrice); the
			// ReceiptItem domain constructor enforces this when the service maps it back.
			ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
			item.Quantity = 1m;
			item.UnitPrice = 50m;
			item.TotalAmount = 50m;

			setup.Receipts.Add(receipt);
			setup.Accounts.Add(account);
			setup.Cards.Add(card);
			setup.ReceiptItems.Add(item);
			await setup.SaveChangesAsync();

			receiptId = receipt.Id;
			accountId = account.Id;
			cardId = card.Id;
		}

		CreateTransactionCommandHandler handler = BuildHandler();

		CreateTransactionCommand Command() => new(
			[new Transaction(Guid.NewGuid(), cardId, new Money(50), DateOnly.FromDateTime(DateTime.Now)) { AccountId = accountId }],
			receiptId);

		// Act — both requests are in flight before either awaits, so they contend on the row lock.
		Task<Exception?> first = CaptureAsync(handler.Handle(Command(), CancellationToken.None).AsTask());
		Task<Exception?> second = CaptureAsync(handler.Handle(Command(), CancellationToken.None).AsTask());
		Exception?[] outcomes = await Task.WhenAll(first, second);

		// Assert — exactly one committed; the loser saw the winner's write and failed validation.
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
