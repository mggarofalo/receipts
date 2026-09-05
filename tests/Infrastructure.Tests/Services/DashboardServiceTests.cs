using Application.Models.Dashboard;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Tests.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

public class DashboardServiceTests
{
	[Fact]
	public async Task GetSpendingOverTimeAsync_MonthlyGranularity_ReturnsCorrectBuckets()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId1 = Guid.NewGuid();
		Guid receiptId2 = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();

		DateOnly day1 = new(2025, 3, 1);
		DateOnly day2 = new(2025, 3, 2);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.AddRange(
				new ReceiptEntity { Id = receiptId1, Location = "Store A", Date = day1, TaxAmount = 0 },
				new ReceiptEntity { Id = receiptId2, Location = "Store B", Date = day2, TaxAmount = 0 });

			context.Transactions.AddRange(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId1, CardId = accountId, Amount = 50.00m, Date = day1 },
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId1, CardId = accountId, Amount = 25.00m, Date = day1 },
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId2, CardId = accountId, Amount = 100.00m, Date = day2 });

			SeedOriginatingCards(context);
			await context.SaveChangesAsync();
		}

		DashboardService service = new(contextFactory);

		// Act
		SpendingOverTimeResult result = await service.GetSpendingOverTimeAsync(
			new DateOnly(2025, 3, 1),
			new DateOnly(2025, 3, 31),
			"monthly",
			CancellationToken.None);

		// Assert
		result.Buckets.Should().ContainSingle();
		result.Buckets[0].Period.Should().Be("2025-03");
		result.Buckets[0].Amount.Should().Be(175.00m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingOverTimeAsync_MonthlyGranularity_EmptyRange_ReturnsEmptyBuckets()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		DashboardService service = new(contextFactory);

		// Act
		SpendingOverTimeResult result = await service.GetSpendingOverTimeAsync(
			new DateOnly(2025, 1, 1),
			new DateOnly(2025, 1, 31),
			"monthly",
			CancellationToken.None);

		// Assert
		result.Buckets.Should().BeEmpty();

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingOverTimeAsync_MonthlyGranularity_MultipleTransactionsSameMonth_AggregatesCorrectly()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 6, 15);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store", Date = date, TaxAmount = 0 });

			context.Transactions.AddRange(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, CardId = accountId, Amount = 10.00m, Date = date },
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, CardId = accountId, Amount = 20.00m, Date = date },
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, CardId = accountId, Amount = 30.00m, Date = date });

			SeedOriginatingCards(context);
			await context.SaveChangesAsync();
		}

		DashboardService service = new(contextFactory);

		// Act
		SpendingOverTimeResult result = await service.GetSpendingOverTimeAsync(
			new DateOnly(2025, 6, 1),
			new DateOnly(2025, 6, 30),
			"monthly",
			CancellationToken.None);

		// Assert
		result.Buckets.Should().ContainSingle();
		result.Buckets[0].Period.Should().Be("2025-06");
		result.Buckets[0].Amount.Should().Be(60.00m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingOverTimeAsync_QuarterlyGranularity_ReturnsCorrectBuckets()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId1 = Guid.NewGuid();
		Guid receiptId2 = Guid.NewGuid();
		Guid receiptId3 = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.AddRange(
				new ReceiptEntity { Id = receiptId1, Location = "Store A", Date = new DateOnly(2025, 1, 15), TaxAmount = 0 },
				new ReceiptEntity { Id = receiptId2, Location = "Store B", Date = new DateOnly(2025, 4, 10), TaxAmount = 0 },
				new ReceiptEntity { Id = receiptId3, Location = "Store C", Date = new DateOnly(2025, 7, 20), TaxAmount = 0 });

			context.Transactions.AddRange(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId1, CardId = accountId, Amount = 100.00m, Date = new DateOnly(2025, 1, 15) },
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId2, CardId = accountId, Amount = 200.00m, Date = new DateOnly(2025, 4, 10) },
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId3, CardId = accountId, Amount = 300.00m, Date = new DateOnly(2025, 7, 20) });

			SeedOriginatingCards(context);
			await context.SaveChangesAsync();
		}

		DashboardService service = new(contextFactory);

		// Act
		SpendingOverTimeResult result = await service.GetSpendingOverTimeAsync(
			new DateOnly(2025, 1, 1),
			new DateOnly(2025, 12, 31),
			"quarterly",
			CancellationToken.None);

		// Assert
		result.Buckets.Should().HaveCount(3);
		result.Buckets[0].Period.Should().Be("2025 Q1");
		result.Buckets[0].Amount.Should().Be(100.00m);
		result.Buckets[1].Period.Should().Be("2025 Q2");
		result.Buckets[1].Amount.Should().Be(200.00m);
		result.Buckets[2].Period.Should().Be("2025 Q3");
		result.Buckets[2].Amount.Should().Be(300.00m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingOverTimeAsync_YtdGranularity_ReturnsMonthlyBuckets()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId1 = Guid.NewGuid();
		Guid receiptId2 = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.AddRange(
				new ReceiptEntity { Id = receiptId1, Location = "Store A", Date = new DateOnly(2025, 1, 10), TaxAmount = 0 },
				new ReceiptEntity { Id = receiptId2, Location = "Store B", Date = new DateOnly(2025, 2, 15), TaxAmount = 0 });

			context.Transactions.AddRange(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId1, CardId = accountId, Amount = 50.00m, Date = new DateOnly(2025, 1, 10) },
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId2, CardId = accountId, Amount = 75.00m, Date = new DateOnly(2025, 2, 15) });

			SeedOriginatingCards(context);
			await context.SaveChangesAsync();
		}

		DashboardService service = new(contextFactory);

		// Act
		SpendingOverTimeResult result = await service.GetSpendingOverTimeAsync(
			new DateOnly(2025, 1, 1),
			new DateOnly(2025, 3, 31),
			"ytd",
			CancellationToken.None);

		// Assert
		result.Buckets.Should().HaveCount(2);
		result.Buckets[0].Period.Should().Be("2025-01");
		result.Buckets[0].Amount.Should().Be(50.00m);
		result.Buckets[1].Period.Should().Be("2025-02");
		result.Buckets[1].Amount.Should().Be(75.00m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingOverTimeAsync_YearlyGranularity_ReturnsCorrectBuckets()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId1 = Guid.NewGuid();
		Guid receiptId2 = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.AddRange(
				new ReceiptEntity { Id = receiptId1, Location = "Store A", Date = new DateOnly(2024, 6, 15), TaxAmount = 0 },
				new ReceiptEntity { Id = receiptId2, Location = "Store B", Date = new DateOnly(2025, 3, 10), TaxAmount = 0 });

			context.Transactions.AddRange(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId1, CardId = accountId, Amount = 100.00m, Date = new DateOnly(2024, 6, 15) },
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId2, CardId = accountId, Amount = 200.00m, Date = new DateOnly(2025, 3, 10) });

			SeedOriginatingCards(context);
			await context.SaveChangesAsync();
		}

		DashboardService service = new(contextFactory);

		// Act
		SpendingOverTimeResult result = await service.GetSpendingOverTimeAsync(
			new DateOnly(2024, 1, 1),
			new DateOnly(2025, 12, 31),
			"yearly",
			CancellationToken.None);

		// Assert
		result.Buckets.Should().HaveCount(2);
		result.Buckets[0].Period.Should().Be("2024");
		result.Buckets[0].Amount.Should().Be(100.00m);
		result.Buckets[1].Period.Should().Be("2025");
		result.Buckets[1].Amount.Should().Be(200.00m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetEarliestReceiptYearAsync_ReturnsEarliestYear()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId1 = Guid.NewGuid();
		Guid receiptId2 = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.AddRange(
				new ReceiptEntity { Id = receiptId1, Location = "Store A", Date = new DateOnly(2022, 3, 1), TaxAmount = 0 },
				new ReceiptEntity { Id = receiptId2, Location = "Store B", Date = new DateOnly(2025, 1, 15), TaxAmount = 0 });

			SeedOriginatingCards(context);
			await context.SaveChangesAsync();
		}

		DashboardService service = new(contextFactory);

		// Act
		int result = await service.GetEarliestReceiptYearAsync(CancellationToken.None);

		// Assert
		result.Should().Be(2022);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetEarliestReceiptYearAsync_ReturnsCurrentYear_WhenNoReceipts()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		DashboardService service = new(contextFactory);

		// Act
		int result = await service.GetEarliestReceiptYearAsync(CancellationToken.None);

		// Assert
		result.Should().Be(DateTime.Today.Year);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingByStoreAsync_GroupsByLocation_ReturnsCorrectResults()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId1 = Guid.NewGuid();
		Guid receiptId2 = Guid.NewGuid();
		Guid receiptId3 = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.AddRange(
				new ReceiptEntity { Id = receiptId1, Location = "Walmart", Date = new DateOnly(2025, 3, 1), TaxAmount = 0 },
				new ReceiptEntity { Id = receiptId2, Location = "Walmart", Date = new DateOnly(2025, 3, 15), TaxAmount = 0 },
				new ReceiptEntity { Id = receiptId3, Location = "Target", Date = new DateOnly(2025, 3, 10), TaxAmount = 0 });

			context.Transactions.AddRange(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId1, CardId = accountId, Amount = 50.00m, Date = new DateOnly(2025, 3, 1) },
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId2, CardId = accountId, Amount = 75.00m, Date = new DateOnly(2025, 3, 15) },
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId3, CardId = accountId, Amount = 100.00m, Date = new DateOnly(2025, 3, 10) });

			SeedOriginatingCards(context);
			await context.SaveChangesAsync();
		}

		DashboardService service = new(contextFactory);

		// Act
		SpendingByStoreResult result = await service.GetSpendingByStoreAsync(
			new DateOnly(2025, 3, 1),
			new DateOnly(2025, 3, 31),
			CancellationToken.None);

		// Assert
		result.Items.Should().HaveCount(2);
		result.Items[0].Location.Should().Be("Walmart");
		result.Items[0].VisitCount.Should().Be(2);
		result.Items[0].TotalAmount.Should().Be(125.00m);
		result.Items[0].AveragePerVisit.Should().Be(62.50m);
		result.Items[1].Location.Should().Be("Target");
		result.Items[1].VisitCount.Should().Be(1);
		result.Items[1].TotalAmount.Should().Be(100.00m);
		result.Items[1].AveragePerVisit.Should().Be(100.00m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingByStoreAsync_EmptyRange_ReturnsEmptyItems()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		DashboardService service = new(contextFactory);

		// Act
		SpendingByStoreResult result = await service.GetSpendingByStoreAsync(
			new DateOnly(2025, 1, 1),
			new DateOnly(2025, 1, 31),
			CancellationToken.None);

		// Assert
		result.Items.Should().BeEmpty();

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingByStoreAsync_OrdersByTotalAmountDescending()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId1 = Guid.NewGuid();
		Guid receiptId2 = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.AddRange(
				new ReceiptEntity { Id = receiptId1, Location = "Small Store", Date = new DateOnly(2025, 3, 1), TaxAmount = 0 },
				new ReceiptEntity { Id = receiptId2, Location = "Big Store", Date = new DateOnly(2025, 3, 10), TaxAmount = 0 });

			context.Transactions.AddRange(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId1, CardId = accountId, Amount = 10.00m, Date = new DateOnly(2025, 3, 1) },
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId2, CardId = accountId, Amount = 500.00m, Date = new DateOnly(2025, 3, 10) });

			SeedOriginatingCards(context);
			await context.SaveChangesAsync();
		}

		DashboardService service = new(contextFactory);

		// Act
		SpendingByStoreResult result = await service.GetSpendingByStoreAsync(
			new DateOnly(2025, 3, 1),
			new DateOnly(2025, 3, 31),
			CancellationToken.None);

		// Assert
		result.Items.Should().HaveCount(2);
		result.Items[0].Location.Should().Be("Big Store");
		result.Items[0].TotalAmount.Should().Be(500.00m);
		result.Items[1].Location.Should().Be("Small Store");
		result.Items[1].TotalAmount.Should().Be(10.00m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingByAccountAsync_ResolvesNamesFromAccountsTable_NotCards()
	{
		// Regression test for RECEIPTS-546: the service used to resolve names by
		// joining Transactions.AccountId against the Cards table. Transactions
		// reference the logical Accounts table (RECEIPTS-543 Stage 2), and this
		// join silently breaks once a card has been merged into another account
		// and its 1:1 account row has been deleted.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Accounts.Add(new AccountEntity
			{
				Id = accountId,
				Name = "Chase Sapphire",
				IsActive = true,
			});

			context.Receipts.Add(new ReceiptEntity
			{
				Id = receiptId,
				Location = "Costco",
				Date = new DateOnly(2025, 5, 1),
				TaxAmount = 0,
			});

			context.Transactions.Add(new TransactionEntity
			{
				Id = Guid.NewGuid(),
				ReceiptId = receiptId,
				CardId = accountId,
				Amount = 42.00m,
				Date = new DateOnly(2025, 5, 1),
			});

			SeedOriginatingCards(context);
			await context.SaveChangesAsync();
		}

		DashboardService service = new(contextFactory);

		// Act
		SpendingByAccountResult result = await service.GetSpendingByAccountAsync(
			new DateOnly(2025, 5, 1),
			new DateOnly(2025, 5, 31),
			CancellationToken.None);

		// Assert
		result.Items.Should().ContainSingle();
		result.Items[0].AccountId.Should().Be(accountId);
		result.Items[0].AccountName.Should().Be("Chase Sapphire");
		result.Items[0].Amount.Should().Be(42.00m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingByAccountAsync_MergedAccount_RollsUpTransactionsFromAllCards()
	{
		// Simulates the Stage 3 post-merge state: two Cards have been merged
		// into a single Account, so all of their transactions reference the
		// surviving account. The widget should show a single row.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid survivingAccountId = Guid.NewGuid();
		Guid receiptId1 = Guid.NewGuid();
		Guid receiptId2 = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Accounts.Add(new AccountEntity
			{
				Id = survivingAccountId,
				Name = "Apple Card",
				IsActive = true,
			});

			context.Receipts.AddRange(
				new ReceiptEntity { Id = receiptId1, Location = "Store A", Date = new DateOnly(2025, 6, 1), TaxAmount = 0 },
				new ReceiptEntity { Id = receiptId2, Location = "Store B", Date = new DateOnly(2025, 6, 2), TaxAmount = 0 });

			context.Transactions.AddRange(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId1, CardId = survivingAccountId, Amount = 10.00m, Date = new DateOnly(2025, 6, 1) },
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId2, CardId = survivingAccountId, Amount = 20.00m, Date = new DateOnly(2025, 6, 2) });

			SeedOriginatingCards(context);
			await context.SaveChangesAsync();
		}

		DashboardService service = new(contextFactory);

		// Act
		SpendingByAccountResult result = await service.GetSpendingByAccountAsync(
			new DateOnly(2025, 6, 1),
			new DateOnly(2025, 6, 30),
			CancellationToken.None);

		// Assert
		result.Items.Should().ContainSingle();
		result.Items[0].AccountName.Should().Be("Apple Card");
		result.Items[0].Amount.Should().Be(30.00m);
		result.Items[0].Percentage.Should().Be(100m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingByAccountAsync_EmptyRange_ReturnsEmptyItems()
	{
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		DashboardService service = new(contextFactory);

		// Act
		SpendingByAccountResult result = await service.GetSpendingByAccountAsync(
			new DateOnly(2025, 1, 1),
			new DateOnly(2025, 1, 31),
			CancellationToken.None);

		// Assert
		result.Items.Should().BeEmpty();

		contextFactory.ResetDatabase();
	}
	private static void SeedOriginatingCards(ApplicationDbContext context)
	{
		foreach (Guid cardId in context.ChangeTracker.Entries<TransactionEntity>().Select(entry => entry.Entity.CardId).Distinct().ToList())
		{
			if (context.Cards.Find(cardId) is null)
			{
				context.Cards.Add(new CardEntity { Id = cardId, AccountId = cardId, Name = "Report card", CardCode = "1234" });
			}
		}
	}

}
