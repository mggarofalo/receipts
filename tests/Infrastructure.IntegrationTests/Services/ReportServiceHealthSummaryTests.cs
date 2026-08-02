using Application.Models.Reports;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

// Postgres-only coverage for RECEIPTS-839's GetHealthSummaryAsync. Two of its three counts are
// exactly the shapes this suite exists to guard (see OutOfBalanceReportTests / RECEIPTS-791):
// the out-of-balance count aggregates correlated `?? 0m` subqueries inside a WHERE, and the
// duplicate-group count is a GROUP BY ... HAVING COUNT(*) > 1 wrapped in an outer COUNT. The
// InMemory unit suite client-evaluates both, so it can never catch a translation regression;
// only a real relational provider throws.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ReportServiceHealthSummaryTests(PostgresFixture fixture)
{
	[Fact]
	public async Task GetHealthSummaryAsync_TranslatesEveryCountToSql_AndReturnsCorrectTotals()
	{
		// Arrange
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();

		Guid unbalanced = Guid.NewGuid();        // items 30, transaction 100 => difference -70
		Guid balanced = Guid.NewGuid();          // items 50, transaction 50  => excluded
		Guid uncategorizedHost = Guid.NewGuid(); // items 1 + 2, transaction 3 => balanced, 2 uncategorized

		DateOnly duplicateDate = new(2025, 6, 1);
		DateOnly otherDate = new(2025, 6, 2);

		await SeedAsync(accountId, cardId, seed =>
		{
			AddReceipt(seed, unbalanced, new DateOnly(2025, 1, 1), "Solo Store A", tax: 0m);
			AddItem(seed, unbalanced, 30m, "Food");
			AddTransaction(seed, unbalanced, accountId, cardId, 100m);

			AddReceipt(seed, balanced, new DateOnly(2025, 1, 2), "Solo Store B", tax: 0m);
			AddItem(seed, balanced, 50m, "Food");
			AddTransaction(seed, balanced, accountId, cardId, 50m);

			AddReceipt(seed, uncategorizedHost, new DateOnly(2025, 1, 3), "Solo Store C", tax: 0m);
			AddItem(seed, uncategorizedHost, 1m, "Uncategorized");
			AddItem(seed, uncategorizedHost, 2m, "Uncategorized");
			AddTransaction(seed, uncategorizedHost, accountId, cardId, 3m);

			// Three receipts sharing date + location collapse into ONE duplicate group...
			for (int i = 0; i < 3; i++)
			{
				AddReceipt(seed, Guid.NewGuid(), duplicateDate, "Duplicate Store", tax: 0m);
			}

			// ...and two on a different date + location make a second.
			for (int i = 0; i < 2; i++)
			{
				AddReceipt(seed, Guid.NewGuid(), otherDate, "Another Store", tax: 0m);
			}

			// A lone receipt on its own date + location is not a group. These duplicate receipts
			// carry no items and no transactions, so 0 == 0 keeps them out of the balance count.
			AddReceipt(seed, Guid.NewGuid(), new DateOnly(2025, 6, 3), "Lone Store", tax: 0m);
		});

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act — if any of the three counts failed to translate, EF throws here rather than
		// silently evaluating on the client (the RECEIPTS-791 failure mode).
		ReportsHealthSummaryResult result = await service.GetHealthSummaryAsync(CancellationToken.None);

		// Assert
		result.OutOfBalanceCount.Should().Be(1);

		// Groups, not receipts: the 3 + 2 duplicate receipts collapse into 2 groups.
		result.DuplicateGroupCount.Should().Be(2);

		result.UncategorizedItemCount.Should().Be(2);
	}

	[Fact]
	public async Task GetHealthSummaryAsync_ReturnsZeros_WhenNothingNeedsAttention()
	{
		// Arrange — one balanced receipt with a categorized item and a unique date + location.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();

		await SeedAsync(accountId, cardId, seed =>
		{
			AddReceipt(seed, receiptId, new DateOnly(2025, 7, 4), "Only Store", tax: 2m);
			AddItem(seed, receiptId, 8m, "Food");
			AddTransaction(seed, receiptId, accountId, cardId, 10m);
		});

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act
		ReportsHealthSummaryResult result = await service.GetHealthSummaryAsync(CancellationToken.None);

		// Assert
		result.OutOfBalanceCount.Should().Be(0);
		result.DuplicateGroupCount.Should().Be(0);
		result.UncategorizedItemCount.Should().Be(0);
	}

	private async Task SeedAsync(Guid accountId, Guid cardId, Action<ApplicationDbContext> seed)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();

		// The summary counts across ALL receipts globally, so isolate from other tests in the collection.
		await context.Database.ExecuteSqlRawAsync(
			"""TRUNCATE "Transactions", "ReceiptItems", "Adjustments", "Receipts" RESTART IDENTITY CASCADE;""");

		// Transactions need valid Account + Card FKs (both Restrict, NOT NULL).
		AccountEntity account = AccountEntityGenerator.Generate();
		account.Id = accountId;
		CardEntity card = CardEntityGenerator.Generate();
		card.Id = cardId;
		card.AccountId = accountId;
		context.Accounts.Add(account);
		context.Cards.Add(card);
		await context.SaveChangesAsync();

		seed(context);
		await context.SaveChangesAsync();
	}

	private static void AddReceipt(ApplicationDbContext context, Guid receiptId, DateOnly date, string location, decimal tax)
	{
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.Id = receiptId;
		receipt.Date = date;
		receipt.Location = location;
		receipt.TaxAmount = tax;
		context.Receipts.Add(receipt);
	}

	private static void AddItem(ApplicationDbContext context, Guid receiptId, decimal totalAmount, string category)
	{
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receiptId);
		item.Quantity = 1m;
		item.UnitPrice = totalAmount;
		item.TotalAmount = totalAmount;
		item.Category = category;
		context.ReceiptItems.Add(item);
	}

	private static void AddTransaction(ApplicationDbContext context, Guid receiptId, Guid accountId, Guid cardId, decimal amount)
	{
		TransactionEntity transaction = TransactionEntityGenerator.Generate(receiptId, accountId, cardId);
		transaction.Amount = amount;
		context.Transactions.Add(transaction);
	}

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
