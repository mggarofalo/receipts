using Application.Models.Reports;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

// Postgres-only coverage for RECEIPTS-791 (PR #612): GetOutOfBalanceAsync must translate its
// aggregates — including SumAsync(x => Math.Abs(difference)) over correlated `?? 0m` subqueries —
// fully to SQL, and paginate in SQL. The InMemory unit suite silently client-evaluates all of this,
// so it can never catch a translation regression; only a real relational provider throws.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class OutOfBalanceReportTests(PostgresFixture fixture)
{
	[Fact]
	public async Task GetOutOfBalanceAsync_TranslatesAggregatesToSql_AndReturnsCorrectTotals()
	{
		// Arrange — three out-of-balance receipts and one balanced (excluded) receipt.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		Guid r1 = Guid.NewGuid(); // items 100, tax 10, adj +5, tx 100 => expected 115, diff +15
		Guid r2 = Guid.NewGuid(); // items 50,  tax 0,  no adj, tx 50  => expected 50,  diff 0 (excluded)
		Guid r3 = Guid.NewGuid(); // items 30,  tax 0,  no adj, tx 100 => expected 30,  diff -70
		Guid r4 = Guid.NewGuid(); // items 20,  tax 0,  no adj, no tx  => expected 20,  diff +20

		await SeedAsync(accountId, cardId, seed =>
		{
			AddReceipt(seed, r1, new DateOnly(2025, 2, 1), tax: 10m);
			AddItem(seed, r1, 100m);
			AddAdjustment(seed, r1, 5m);
			AddTransaction(seed, r1, accountId, cardId, 100m);

			AddReceipt(seed, r2, new DateOnly(2025, 2, 15), tax: 0m);
			AddItem(seed, r2, 50m);
			AddTransaction(seed, r2, accountId, cardId, 50m);

			AddReceipt(seed, r3, new DateOnly(2025, 1, 1), tax: 0m);
			AddItem(seed, r3, 30m);
			AddTransaction(seed, r3, accountId, cardId, 100m);

			AddReceipt(seed, r4, new DateOnly(2025, 3, 1), tax: 0m);
			AddItem(seed, r4, 20m);
		});

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act — default sort (date asc). If any aggregate/projection failed to translate, EF would
		// throw here rather than silently evaluate on the client (RECEIPTS-791).
		OutOfBalanceResult result = await service.GetOutOfBalanceAsync(
			sortBy: "date", sortDirection: "asc", page: 1, pageSize: 50, CancellationToken.None);

		// Assert — only the three out-of-balance receipts, ordered by date asc.
		result.TotalCount.Should().Be(3);
		result.Items.Select(i => i.ReceiptId).Should().Equal(r3, r1, r4);

		// TotalDiscrepancy = SUM(ABS(difference)) computed in SQL = |−70| + |15| + |20| = 105.
		result.TotalDiscrepancy.Should().Be(105m);

		OutOfBalanceItem item1 = result.Items.Single(i => i.ReceiptId == r1);
		item1.ItemSubtotal.Should().Be(100m);
		item1.TaxAmount.Should().Be(10m);
		item1.AdjustmentTotal.Should().Be(5m);
		item1.ExpectedTotal.Should().Be(115m);
		item1.TransactionTotal.Should().Be(100m);
		item1.Difference.Should().Be(15m);

		result.Items.Single(i => i.ReceiptId == r3).Difference.Should().Be(-70m);
		result.Items.Single(i => i.ReceiptId == r4).Difference.Should().Be(20m);
	}

	[Fact]
	public async Task GetOutOfBalanceAsync_PaginatesInSql_WithStableOrdering()
	{
		// Arrange — three out-of-balance receipts with distinct differences for a deterministic sort.
		Guid accountId = Guid.NewGuid();
		Guid cardId = Guid.NewGuid();
		Guid rSmall = Guid.NewGuid();  // diff +10
		Guid rMedium = Guid.NewGuid(); // diff +20
		Guid rLarge = Guid.NewGuid();  // diff +30

		await SeedAsync(accountId, cardId, seed =>
		{
			AddReceipt(seed, rSmall, new DateOnly(2025, 5, 1), tax: 0m);
			AddItem(seed, rSmall, 10m);

			AddReceipt(seed, rMedium, new DateOnly(2025, 5, 2), tax: 0m);
			AddItem(seed, rMedium, 20m);

			AddReceipt(seed, rLarge, new DateOnly(2025, 5, 3), tax: 0m);
			AddItem(seed, rLarge, 30m);
		});

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act — sort by difference desc, page size 2. Page 1 fetches only 2 rows from the DB even
		// though TotalCount is 3, proving Skip/Take runs in SQL (not an in-memory slice of all rows).
		OutOfBalanceResult page1 = await service.GetOutOfBalanceAsync(
			sortBy: "difference", sortDirection: "desc", page: 1, pageSize: 2, CancellationToken.None);
		OutOfBalanceResult page2 = await service.GetOutOfBalanceAsync(
			sortBy: "difference", sortDirection: "desc", page: 2, pageSize: 2, CancellationToken.None);

		// Assert — pagination and ordering.
		page1.TotalCount.Should().Be(3);
		page1.Items.Should().HaveCount(2);
		page1.Items.Select(i => i.ReceiptId).Should().Equal(rLarge, rMedium);

		page2.TotalCount.Should().Be(3);
		page2.Items.Should().ContainSingle().Which.ReceiptId.Should().Be(rSmall);

		// The full discrepancy is aggregated over the whole set regardless of which page is requested.
		page1.TotalDiscrepancy.Should().Be(60m);
		page2.TotalDiscrepancy.Should().Be(60m);
	}

	private async Task SeedAsync(Guid accountId, Guid cardId, Action<ApplicationDbContext> seed)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();

		// The report reads across ALL receipts globally, so isolate from other tests in the collection.
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

	private static void AddReceipt(ApplicationDbContext context, Guid receiptId, DateOnly date, decimal tax)
	{
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.Id = receiptId;
		receipt.Date = date;
		receipt.TaxAmount = tax;
		context.Receipts.Add(receipt);
	}

	private static void AddItem(ApplicationDbContext context, Guid receiptId, decimal totalAmount)
	{
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receiptId);
		item.Quantity = 1m;
		item.UnitPrice = totalAmount;
		item.TotalAmount = totalAmount;
		context.ReceiptItems.Add(item);
	}

	private static void AddAdjustment(ApplicationDbContext context, Guid receiptId, decimal amount)
	{
		AdjustmentEntity adjustment = AdjustmentEntityGenerator.Generate();
		adjustment.ReceiptId = receiptId;
		adjustment.Amount = amount;
		context.Adjustments.Add(adjustment);
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
