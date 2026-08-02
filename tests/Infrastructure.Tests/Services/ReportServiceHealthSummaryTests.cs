using Application.Models.Reports;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Tests.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

public class ReportServiceHealthSummaryTests
{
	[Fact]
	public async Task GetHealthSummaryAsync_ReturnsZerosForAnEmptyDatabase()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		ReportService service = new(contextFactory);

		// Act
		ReportsHealthSummaryResult result = await service.GetHealthSummaryAsync(CancellationToken.None);

		// Assert
		result.OutOfBalanceCount.Should().Be(0);
		result.DuplicateGroupCount.Should().Be(0);
		result.UncategorizedItemCount.Should().Be(0);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetHealthSummaryAsync_CountsOnlyOutOfBalanceReceipts()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid balancedId = Guid.NewGuid();
		Guid unbalancedId = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 3, 1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// Balanced: items 10 + tax 1 = 11, transaction 11.
			context.Receipts.Add(new ReceiptEntity { Id = balancedId, Location = "Store A", Date = date, TaxAmount = 1.00m });
			context.ReceiptItems.Add(new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = balancedId, Description = "Item", Quantity = 1, UnitPrice = 10.00m, TotalAmount = 10.00m, Category = "Food" });
			context.Transactions.Add(new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = balancedId, AccountId = accountId, Amount = 11.00m, Date = date });

			// Unbalanced: items 10 + tax 1 = 11, transaction 15.
			context.Receipts.Add(new ReceiptEntity { Id = unbalancedId, Location = "Store B", Date = date.AddDays(1), TaxAmount = 1.00m });
			context.ReceiptItems.Add(new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = unbalancedId, Description = "Item", Quantity = 1, UnitPrice = 10.00m, TotalAmount = 10.00m, Category = "Food" });
			context.Transactions.Add(new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = unbalancedId, AccountId = accountId, Amount = 15.00m, Date = date.AddDays(1) });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		ReportsHealthSummaryResult result = await service.GetHealthSummaryAsync(CancellationToken.None);

		// Assert
		result.OutOfBalanceCount.Should().Be(1);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetHealthSummaryAsync_ExcludesSoftDeletedRecords()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid deletedReceiptId = Guid.NewGuid();
		DateOnly date = new(2025, 4, 1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// Soft-deleted, unbalanced receipt (items 10, no transaction) — must not be counted.
			context.Receipts.Add(new ReceiptEntity { Id = deletedReceiptId, Location = "Store A", Date = date, TaxAmount = 0m, DeletedAt = DateTimeOffset.UtcNow });
			context.ReceiptItems.Add(new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = deletedReceiptId, Description = "Item", Quantity = 1, UnitPrice = 10.00m, TotalAmount = 10.00m, Category = "Food" });

			// Soft-deleted uncategorized item — must not be counted.
			context.ReceiptItems.Add(new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = Guid.NewGuid(), Description = "Ghost", Quantity = 1, UnitPrice = 1.00m, TotalAmount = 1.00m, Category = "Uncategorized", DeletedAt = DateTimeOffset.UtcNow });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		ReportsHealthSummaryResult result = await service.GetHealthSummaryAsync(CancellationToken.None);

		// Assert
		result.OutOfBalanceCount.Should().Be(0);
		result.DuplicateGroupCount.Should().Be(0);
		result.UncategorizedItemCount.Should().Be(0);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetHealthSummaryAsync_CountsDuplicateGroupsNotDuplicateReceipts()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		DateOnly date = new(2025, 5, 1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// Three receipts on the same date+location = ONE duplicate group.
			for (int i = 0; i < 3; i++)
			{
				context.Receipts.Add(new ReceiptEntity { Id = Guid.NewGuid(), Location = "Store A", Date = date, TaxAmount = 0m });
			}

			// A second date+location pair with two receipts = a second group.
			for (int i = 0; i < 2; i++)
			{
				context.Receipts.Add(new ReceiptEntity { Id = Guid.NewGuid(), Location = "Store B", Date = date, TaxAmount = 0m });
			}

			// A lone receipt is not a group.
			context.Receipts.Add(new ReceiptEntity { Id = Guid.NewGuid(), Location = "Store C", Date = date, TaxAmount = 0m });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		ReportsHealthSummaryResult result = await service.GetHealthSummaryAsync(CancellationToken.None);

		// Assert
		result.DuplicateGroupCount.Should().Be(2);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetHealthSummaryAsync_CountsOnlyUncategorizedItems()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.ReceiptItems.Add(new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "A", Quantity = 1, UnitPrice = 1m, TotalAmount = 1m, Category = "Uncategorized" });
			context.ReceiptItems.Add(new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "B", Quantity = 1, UnitPrice = 1m, TotalAmount = 1m, Category = "Uncategorized" });
			context.ReceiptItems.Add(new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "C", Quantity = 1, UnitPrice = 1m, TotalAmount = 1m, Category = "Food" });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		ReportsHealthSummaryResult result = await service.GetHealthSummaryAsync(CancellationToken.None);

		// Assert
		result.UncategorizedItemCount.Should().Be(2);

		contextFactory.ResetDatabase();
	}
}
