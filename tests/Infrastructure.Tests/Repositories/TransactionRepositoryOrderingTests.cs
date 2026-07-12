using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.Tests.Repositories;

// RECEIPTS-803 — guards the RECEIPTS-752 data-loss fix at its root.
//
// The YNAB push assigns each transaction an import_id whose occurrence suffix is derived from the
// ORDER in which same-amount/same-date siblings are enumerated (PushYnabTransactionsCommandHandler
// numbers occurrences while iterating the split result, which the split calculator produces via a
// STABLE OrderByDescending that preserves the order the repository returns rows in). For a retry
// after a partial push to remain safe, a given transaction must therefore always land on the same
// occurrence — and thus the same import_id — no matter what physical order the database yields the
// rows in. That determinism is provided by the terminal `.OrderBy(e => e.Id)` in
// GetWithAccountByReceiptIdAsync.
//
// If a future refactor drops that OrderBy, two same-amount transactions could swap occurrences
// between the first push and a retry, causing the retry to reuse an already-consumed import_id.
// YNAB would 409 and recovery would bind both local transactions to one YNAB transaction, silently
// dropping the second amount. This test fails if the ordering is removed.
public class TransactionRepositoryOrderingTests
{
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

	[Fact]
	public async Task GetWithAccountByReceiptIdAsync_ReturnsTransactionsOrderedByIdAscending_RegardlessOfInsertionOrder()
	{
		// Arrange — parent receipt + account + card.
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		AccountEntity account = AccountEntityGenerator.Generate();
		CardEntity card = CardEntityGenerator.Generate();
		card.AccountId = account.Id;
		card.Id = account.Id;

		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			await seed.Receipts.AddAsync(receipt);
			await seed.Accounts.AddAsync(account);
			await seed.Cards.AddAsync(card);
			await seed.SaveChangesAsync(CancellationToken.None);
		}

		// Insert the transactions in DESCENDING-Id order so the physical/insertion order is the exact
		// reverse of the expected ascending-Id order. Removing the repository's `.OrderBy(e => e.Id)`
		// would surface them in insertion (descending) order and break the ascending assertions below.
		const int transactionCount = 5;
		List<TransactionEntity> entities = TransactionEntityGenerator.GenerateList(transactionCount, receipt.Id, account.Id);
		List<TransactionEntity> insertionOrder = [.. entities.OrderByDescending(e => e.Id)];

		using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			await seed.Transactions.AddRangeAsync(insertionOrder);
			await seed.SaveChangesAsync(CancellationToken.None);
		}

		TransactionRepository repository = new(_contextFactory);

		// Act
		List<TransactionEntity> actual = await repository.GetWithAccountByReceiptIdAsync(receipt.Id, CancellationToken.None);

		// Assert — deterministic ascending-Id order, independent of the order rows were inserted in.
		actual.Should().HaveCount(transactionCount);
		actual.Select(t => t.Id).Should().BeInAscendingOrder("import_id occurrence numbering relies on a deterministic row order");
		actual.Select(t => t.Id).Should().Equal(entities.Select(e => e.Id).OrderBy(id => id));

		_contextFactory.ResetDatabase();
	}
}
