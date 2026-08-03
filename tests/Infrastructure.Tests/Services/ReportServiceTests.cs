using Application.Models.Reports;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Tests.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Tests.Services;

public class ReportServiceTests
{
	[Fact]
	public async Task GetOutOfBalanceAsync_ReturnsEmptyWhenAllBalanced()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 3, 1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store A", Date = date, TaxAmount = 1.00m });

			context.ReceiptItems.Add(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Item 1", Quantity = 1, UnitPrice = 10.00m, TotalAmount = 10.00m, Category = "Food" });

			context.Transactions.Add(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, AccountId = accountId, Amount = 11.00m, Date = date });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		OutOfBalanceResult result = await service.GetOutOfBalanceAsync("date", "asc", 1, 50, CancellationToken.None);

		// Assert
		result.Items.Should().BeEmpty();
		result.TotalCount.Should().Be(0);
		result.TotalDiscrepancy.Should().Be(0m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetOutOfBalanceAsync_ReturnsReceiptsWhereExpectedDoesNotMatchTransaction()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 3, 1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// Receipt: items=10, tax=1, adjustments=0, expected=11, transaction=15
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store A", Date = date, TaxAmount = 1.00m });

			context.ReceiptItems.Add(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Item 1", Quantity = 1, UnitPrice = 10.00m, TotalAmount = 10.00m, Category = "Food" });

			context.Transactions.Add(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, AccountId = accountId, Amount = 15.00m, Date = date });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		OutOfBalanceResult result = await service.GetOutOfBalanceAsync("date", "asc", 1, 50, CancellationToken.None);

		// Assert
		result.Items.Should().ContainSingle();
		result.TotalCount.Should().Be(1);

		OutOfBalanceItem item = result.Items[0];
		item.ReceiptId.Should().Be(receiptId);
		item.Location.Should().Be("Store A");
		item.Date.Should().Be(date);
		item.ItemSubtotal.Should().Be(10.00m);
		item.TaxAmount.Should().Be(1.00m);
		item.AdjustmentTotal.Should().Be(0m);
		item.ExpectedTotal.Should().Be(11.00m);
		item.TransactionTotal.Should().Be(15.00m);
		item.Difference.Should().Be(-4.00m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetOutOfBalanceAsync_IncludesAdjustmentsInExpectedTotal()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 3, 1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// items=10, tax=1, adjustment=2, expected=13, transaction=10 => diff=3
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store B", Date = date, TaxAmount = 1.00m });

			context.ReceiptItems.Add(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Item 1", Quantity = 1, UnitPrice = 10.00m, TotalAmount = 10.00m, Category = "Food" });

			context.Adjustments.Add(
				new AdjustmentEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Type = Common.AdjustmentType.Discount, Amount = 2.00m });

			context.Transactions.Add(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, AccountId = accountId, Amount = 10.00m, Date = date });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		OutOfBalanceResult result = await service.GetOutOfBalanceAsync("date", "asc", 1, 50, CancellationToken.None);

		// Assert
		result.Items.Should().ContainSingle();
		OutOfBalanceItem item = result.Items[0];
		item.AdjustmentTotal.Should().Be(2.00m);
		item.ExpectedTotal.Should().Be(13.00m);
		item.Difference.Should().Be(3.00m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetOutOfBalanceAsync_ExcludesSoftDeletedRecords()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 3, 1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// Soft-deleted receipt should not appear
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Deleted Store", Date = date, TaxAmount = 1.00m, DeletedAt = DateTimeOffset.UtcNow });

			context.ReceiptItems.Add(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Item 1", Quantity = 1, UnitPrice = 10.00m, TotalAmount = 10.00m, Category = "Food" });

			// No transaction — would normally be out of balance
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		OutOfBalanceResult result = await service.GetOutOfBalanceAsync("date", "asc", 1, 50, CancellationToken.None);

		// Assert
		result.Items.Should().BeEmpty();

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetOutOfBalanceAsync_SortsByDateAscByDefault()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();

		Guid receiptId1 = Guid.NewGuid();
		Guid receiptId2 = Guid.NewGuid();
		DateOnly day1 = new(2025, 3, 1);
		DateOnly day2 = new(2025, 3, 2);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// Insert in reverse order
			context.Receipts.AddRange(
				new ReceiptEntity { Id = receiptId2, Location = "B", Date = day2, TaxAmount = 0m },
				new ReceiptEntity { Id = receiptId1, Location = "A", Date = day1, TaxAmount = 0m });

			context.Transactions.AddRange(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId1, AccountId = accountId, Amount = 99.00m, Date = day1 },
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId2, AccountId = accountId, Amount = 99.00m, Date = day2 });

			// No items — so expected=0, transaction=99 => diff=-99 for both
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		OutOfBalanceResult result = await service.GetOutOfBalanceAsync("date", "asc", 1, 50, CancellationToken.None);

		// Assert
		result.Items.Should().HaveCount(2);
		result.Items[0].Date.Should().Be(day1);
		result.Items[1].Date.Should().Be(day2);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetOutOfBalanceAsync_SortsByDifferenceDesc()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();

		Guid receiptId1 = Guid.NewGuid();
		Guid receiptId2 = Guid.NewGuid();
		DateOnly date = new(2025, 3, 1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.AddRange(
				new ReceiptEntity { Id = receiptId1, Location = "A", Date = date, TaxAmount = 0m },
				new ReceiptEntity { Id = receiptId2, Location = "B", Date = date, TaxAmount = 0m });

			// Receipt 1: items=10, transaction=5 => diff=5
			context.ReceiptItems.Add(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId1, Description = "Item", Quantity = 1, UnitPrice = 10.00m, TotalAmount = 10.00m, Category = "Food" });
			context.Transactions.Add(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId1, AccountId = accountId, Amount = 5.00m, Date = date });

			// Receipt 2: items=20, transaction=5 => diff=15
			context.ReceiptItems.Add(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId2, Description = "Item", Quantity = 1, UnitPrice = 20.00m, TotalAmount = 20.00m, Category = "Food" });
			context.Transactions.Add(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId2, AccountId = accountId, Amount = 5.00m, Date = date });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		OutOfBalanceResult result = await service.GetOutOfBalanceAsync("difference", "desc", 1, 50, CancellationToken.None);

		// Assert
		result.Items.Should().HaveCount(2);
		result.Items[0].Difference.Should().Be(15.00m);
		result.Items[1].Difference.Should().Be(5.00m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetOutOfBalanceAsync_PaginatesCorrectly()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// Create 3 out-of-balance receipts
			for (int i = 0; i < 3; i++)
			{
				Guid receiptId = Guid.NewGuid();
				DateOnly date = new(2025, 3, i + 1);

				context.Receipts.Add(
					new ReceiptEntity { Id = receiptId, Location = $"Store {i}", Date = date, TaxAmount = 0m });

				context.Transactions.Add(
					new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, AccountId = accountId, Amount = 99.00m, Date = date });
			}

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act - page 1, size 2
		OutOfBalanceResult page1 = await service.GetOutOfBalanceAsync("date", "asc", 1, 2, CancellationToken.None);

		// Assert
		page1.Items.Should().HaveCount(2);
		page1.TotalCount.Should().Be(3);

		// Act - page 2, size 2
		OutOfBalanceResult page2 = await service.GetOutOfBalanceAsync("date", "asc", 2, 2, CancellationToken.None);

		// Assert
		page2.Items.Should().ContainSingle();
		page2.TotalCount.Should().Be(3);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetOutOfBalanceAsync_CalculatesTotalDiscrepancyAsAbsoluteSum()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 3, 1);

		Guid receiptId1 = Guid.NewGuid();
		Guid receiptId2 = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.AddRange(
				new ReceiptEntity { Id = receiptId1, Location = "A", Date = date, TaxAmount = 0m },
				new ReceiptEntity { Id = receiptId2, Location = "B", Date = date, TaxAmount = 0m });

			// Receipt 1: items=10, transaction=5 => diff=5 (positive)
			context.ReceiptItems.Add(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId1, Description = "Item", Quantity = 1, UnitPrice = 10.00m, TotalAmount = 10.00m, Category = "Food" });
			context.Transactions.Add(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId1, AccountId = accountId, Amount = 5.00m, Date = date });

			// Receipt 2: items=5, transaction=10 => diff=-5 (negative)
			context.ReceiptItems.Add(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId2, Description = "Item", Quantity = 1, UnitPrice = 5.00m, TotalAmount = 5.00m, Category = "Food" });
			context.Transactions.Add(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId2, AccountId = accountId, Amount = 10.00m, Date = date });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		OutOfBalanceResult result = await service.GetOutOfBalanceAsync("date", "asc", 1, 50, CancellationToken.None);

		// Assert
		result.TotalDiscrepancy.Should().Be(10.00m); // |5| + |-5| = 10

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetOutOfBalanceAsync_ExcludesSoftDeletedItemsFromCalculation()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 3, 1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// Receipt with items=10 (active) + 5 (deleted), tax=1, transaction=11
			// Expected with active only: 10+1=11 => balanced
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store", Date = date, TaxAmount = 1.00m });

			context.ReceiptItems.AddRange(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Active", Quantity = 1, UnitPrice = 10.00m, TotalAmount = 10.00m, Category = "Food" },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Deleted", Quantity = 1, UnitPrice = 5.00m, TotalAmount = 5.00m, Category = "Food", DeletedAt = DateTimeOffset.UtcNow });

			context.Transactions.Add(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, AccountId = accountId, Amount = 11.00m, Date = date });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		OutOfBalanceResult result = await service.GetOutOfBalanceAsync("date", "asc", 1, 50, CancellationToken.None);

		// Assert - should be balanced since deleted items are excluded
		result.Items.Should().BeEmpty();

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetOutOfBalanceAsync_HandlesReceiptWithNoItems()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 3, 1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// Receipt with no items, tax=0, transaction=5 => expected=0, diff=-5
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Empty Store", Date = date, TaxAmount = 0m });

			context.Transactions.Add(
				new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, AccountId = accountId, Amount = 5.00m, Date = date });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		OutOfBalanceResult result = await service.GetOutOfBalanceAsync("date", "asc", 1, 50, CancellationToken.None);

		// Assert
		result.Items.Should().ContainSingle();
		result.Items[0].ItemSubtotal.Should().Be(0m);
		result.Items[0].TransactionTotal.Should().Be(5.00m);
		result.Items[0].Difference.Should().Be(-5.00m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetOutOfBalanceAsync_HandlesReceiptWithNoTransactions()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();
		DateOnly date = new(2025, 3, 1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// Receipt with items=10, tax=1, no transaction => expected=11, transaction=0, diff=11
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "No Payment", Date = date, TaxAmount = 1.00m });

			context.ReceiptItems.Add(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Item 1", Quantity = 1, UnitPrice = 10.00m, TotalAmount = 10.00m, Category = "Food" });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		OutOfBalanceResult result = await service.GetOutOfBalanceAsync("date", "asc", 1, 50, CancellationToken.None);

		// Assert
		result.Items.Should().ContainSingle();
		result.Items[0].TransactionTotal.Should().Be(0m);
		result.Items[0].ExpectedTotal.Should().Be(11.00m);
		result.Items[0].Difference.Should().Be(11.00m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetItemDescriptionsAsync_ReturnsMatchingItems()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2025, 3, 1), TaxAmount = 0m });

			context.ReceiptItems.AddRange(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Milk", Quantity = 1, UnitPrice = 3.00m, TotalAmount = 3.00m, Category = "Dairy" },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Milk", Quantity = 1, UnitPrice = 3.50m, TotalAmount = 3.50m, Category = "Dairy" },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Bread", Quantity = 1, UnitPrice = 2.00m, TotalAmount = 2.00m, Category = "Bakery" });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		ItemDescriptionResult result = await service.GetItemDescriptionsAsync("milk", false, 10, CancellationToken.None);

		// Assert
		result.Items.Should().ContainSingle();
		result.Items[0].Description.Should().Be("Milk");
		result.Items[0].Category.Should().Be("Dairy");
		result.Items[0].Occurrences.Should().Be(2);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetItemDescriptionsAsync_CategoryOnlyMode_ReturnsCategories()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2025, 3, 1), TaxAmount = 0m });

			context.ReceiptItems.AddRange(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Milk", Quantity = 1, UnitPrice = 3.00m, TotalAmount = 3.00m, Category = "Dairy" },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Cheese", Quantity = 1, UnitPrice = 5.00m, TotalAmount = 5.00m, Category = "Dairy" },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Bread", Quantity = 1, UnitPrice = 2.00m, TotalAmount = 2.00m, Category = "Bakery" });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		ItemDescriptionResult result = await service.GetItemDescriptionsAsync("dairy", true, 10, CancellationToken.None);

		// Assert
		result.Items.Should().ContainSingle();
		result.Items[0].Description.Should().Be("Dairy");
		result.Items[0].Category.Should().Be("Dairy");
		result.Items[0].Occurrences.Should().Be(2);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetItemDescriptionsAsync_ExcludesSoftDeletedItems()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2025, 3, 1), TaxAmount = 0m });

			context.ReceiptItems.AddRange(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Milk", Quantity = 1, UnitPrice = 3.00m, TotalAmount = 3.00m, Category = "Dairy" },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Milk", Quantity = 1, UnitPrice = 3.00m, TotalAmount = 3.00m, Category = "Dairy", DeletedAt = DateTimeOffset.UtcNow });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		ItemDescriptionResult result = await service.GetItemDescriptionsAsync("milk", false, 10, CancellationToken.None);

		// Assert
		result.Items.Should().ContainSingle();
		result.Items[0].Occurrences.Should().Be(1);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetItemDescriptionsAsync_RespectsLimit()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2025, 3, 1), TaxAmount = 0m });

			context.ReceiptItems.AddRange(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Item A", Quantity = 1, UnitPrice = 1.00m, TotalAmount = 1.00m, Category = "Cat1" },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Item B", Quantity = 1, UnitPrice = 2.00m, TotalAmount = 2.00m, Category = "Cat2" },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Item C", Quantity = 1, UnitPrice = 3.00m, TotalAmount = 3.00m, Category = "Cat3" });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		ItemDescriptionResult result = await service.GetItemDescriptionsAsync("item", false, 2, CancellationToken.None);

		// Assert
		result.Items.Should().HaveCount(2);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetItemDescriptionsAsync_NoMatch_ReturnsEmpty()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2025, 3, 1), TaxAmount = 0m });

			context.ReceiptItems.Add(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Milk", Quantity = 1, UnitPrice = 3.00m, TotalAmount = 3.00m, Category = "Dairy" });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		ItemDescriptionResult result = await service.GetItemDescriptionsAsync("xyz", false, 10, CancellationToken.None);

		// Assert
		result.Items.Should().BeEmpty();

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetItemDescriptionsAsync_GroupsByDescriptionAndCategory()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid receiptId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2025, 3, 1), TaxAmount = 0m });

			// Same description, different categories => separate groups
			context.ReceiptItems.AddRange(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Milk", Quantity = 1, UnitPrice = 3.00m, TotalAmount = 3.00m, Category = "Dairy" },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Milk", Quantity = 1, UnitPrice = 4.00m, TotalAmount = 4.00m, Category = "Beverages" });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		ItemDescriptionResult result = await service.GetItemDescriptionsAsync("milk", false, 10, CancellationToken.None);

		// Assert
		result.Items.Should().HaveCount(2);
		result.Items.Should().Contain(x => x.Category == "Dairy");
		result.Items.Should().Contain(x => x.Category == "Beverages");

		contextFactory.ResetDatabase();
	}

	// ── GetItemCostOverTimeAsync (RECEIPTS-841: normalizedDescription filter) ────────

	[Fact]
	public async Task GetItemCostOverTimeAsync_FiltersByNormalizedDescription_AcrossDifferentRawDescriptions()
	{
		// Arrange — two items with different raw descriptions ("2% Milk" vs "Skim Milk") share the
		// same NormalizedDescriptionId, so filtering by canonical name must return both.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid normalizedId = Guid.NewGuid();
		Guid receipt1 = Guid.NewGuid();
		Guid receipt2 = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Milk",
				Status = Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});

			context.Receipts.AddRange(
				new ReceiptEntity { Id = receipt1, Location = "Store", Date = new DateOnly(2025, 1, 10), TaxAmount = 0m },
				new ReceiptEntity { Id = receipt2, Location = "Store", Date = new DateOnly(2025, 2, 15), TaxAmount = 0m });

			context.ReceiptItems.AddRange(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receipt1, Description = "2% Milk", Quantity = 1, UnitPrice = 3.49m, TotalAmount = 3.49m, Category = "Dairy", NormalizedDescriptionId = normalizedId },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receipt2, Description = "Skim Milk", Quantity = 1, UnitPrice = 3.29m, TotalAmount = 3.29m, Category = "Dairy", NormalizedDescriptionId = normalizedId });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		ItemCostOverTimeResult result = await service.GetItemCostOverTimeAsync(
			description: null, category: null, startDate: null, endDate: null, granularity: "exact",
			normalizedDescription: "Milk", CancellationToken.None);

		// Assert — both raw descriptions are represented because they share the canonical name.
		result.Buckets.Should().HaveCount(2);
		result.Buckets.Select(b => b.Amount).Should().BeEquivalentTo([3.49m, 3.29m]);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetItemCostOverTimeAsync_NormalizedDescriptionFilter_ExcludesItemsWithNullFk()
	{
		// Arrange — an item with no NormalizedDescriptionId must never match a normalizedDescription
		// filter (the LEFT JOIN carries a null CanonicalName for it, which can never equal a filter value).
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid normalizedId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Milk",
				Status = Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});

			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2025, 1, 10), TaxAmount = 0m });

			context.ReceiptItems.AddRange(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Milk", Quantity = 1, UnitPrice = 3.49m, TotalAmount = 3.49m, Category = "Dairy", NormalizedDescriptionId = normalizedId },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Milk (unlinked)", Quantity = 1, UnitPrice = 99.99m, TotalAmount = 99.99m, Category = "Dairy", NormalizedDescriptionId = null });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		ItemCostOverTimeResult result = await service.GetItemCostOverTimeAsync(
			description: null, category: null, startDate: null, endDate: null, granularity: "exact",
			normalizedDescription: "Milk", CancellationToken.None);

		// Assert — only the linked item is included.
		result.Buckets.Should().ContainSingle();
		result.Buckets[0].Amount.Should().Be(3.49m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetItemCostOverTimeAsync_DescriptionTakesPrecedenceOverNormalizedDescription()
	{
		// Arrange — an item matches `description` but belongs to a different normalized bucket than
		// the one requested; another item matches only the normalizedDescription filter. Precedence
		// (description > normalizedDescription > category) means only the description match returns.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();

		Guid milkNormalizedId = Guid.NewGuid();
		Guid receiptId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = milkNormalizedId,
				CanonicalName = "Milk",
				Status = Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});

			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2025, 1, 10), TaxAmount = 0m });

			context.ReceiptItems.AddRange(
				// Matches `description` exactly, and is NOT linked to the "Milk" normalized bucket.
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Oat Milk", Quantity = 1, UnitPrice = 4.99m, TotalAmount = 4.99m, Category = "Dairy", NormalizedDescriptionId = null },
				// Only matches the "Milk" normalizedDescription filter, not the description filter.
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "2% Milk", Quantity = 1, UnitPrice = 3.49m, TotalAmount = 3.49m, Category = "Dairy", NormalizedDescriptionId = milkNormalizedId });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act — both description and normalizedDescription supplied; description must win.
		ItemCostOverTimeResult result = await service.GetItemCostOverTimeAsync(
			description: "Oat Milk", category: null, startDate: null, endDate: null, granularity: "exact",
			normalizedDescription: "Milk", CancellationToken.None);

		// Assert — only the description match is returned.
		result.Buckets.Should().ContainSingle();
		result.Buckets[0].Amount.Should().Be(4.99m);

		contextFactory.ResetDatabase();
	}

	// ── GetSpendingByNormalizedDescriptionAsync ──────────────────────────────

	[Fact]
	public async Task GetSpendingByNormalizedDescriptionAsync_GroupsByCanonicalName_AndBucketsNullFk()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid receiptId = Guid.NewGuid();
		Guid normalizedId = Guid.NewGuid();
		DateOnly date = new(2025, 3, 1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Organic Milk",
				Status = Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});

			context.Receipts.Add(new ReceiptEntity
			{
				Id = receiptId,
				Location = "Store A",
				Date = date,
				TaxAmount = 0m,
			});

			// Two items linked to "Organic Milk"
			context.ReceiptItems.AddRange(
				new ReceiptItemEntity
				{
					Id = Guid.NewGuid(),
					ReceiptId = receiptId,
					Description = "organic milk",
					Quantity = 1,
					UnitPrice = 4.00m,
					TotalAmount = 4.00m,
					Category = "Dairy",
					NormalizedDescriptionId = normalizedId,
				},
				new ReceiptItemEntity
				{
					Id = Guid.NewGuid(),
					ReceiptId = receiptId,
					Description = "ORGANIC MILK",
					Quantity = 1,
					UnitPrice = 5.50m,
					TotalAmount = 5.50m,
					Category = "Dairy",
					NormalizedDescriptionId = normalizedId,
				},
				// One item with no normalized description
				new ReceiptItemEntity
				{
					Id = Guid.NewGuid(),
					ReceiptId = receiptId,
					Description = "Mystery Item",
					Quantity = 1,
					UnitPrice = 2.00m,
					TotalAmount = 2.00m,
					Category = "Uncategorized",
					NormalizedDescriptionId = null,
				});

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		SpendingByNormalizedDescriptionResult result = await service
			.GetSpendingByNormalizedDescriptionAsync(from: null, to: null, "totalAmount", "desc", 1, 50, CancellationToken.None);

		// Assert
		result.Items.Should().HaveCount(2);
		result.FromDate.Should().BeNull();
		result.ToDate.Should().BeNull();
		result.TotalCount.Should().Be(2);
		result.GrandTotal.Should().Be(11.50m);

		SpendingByNormalizedDescriptionItem milkBucket = result.Items.Single(i => i.CanonicalName == "Organic Milk");
		milkBucket.TotalAmount.Should().Be(9.50m);
		milkBucket.ItemCount.Should().Be(2);
		milkBucket.Currency.Should().Be("USD");
		milkBucket.FirstSeen.Should().NotBeNull();
		milkBucket.LastSeen.Should().NotBeNull();

		SpendingByNormalizedDescriptionItem notNormalizedBucket = result.Items.Single(i => i.CanonicalName == "(Not Normalized)");
		notNormalizedBucket.TotalAmount.Should().Be(2.00m);
		notNormalizedBucket.ItemCount.Should().Be(1);

		// Ordered by total desc
		result.Items[0].CanonicalName.Should().Be("Organic Milk");
		result.Items[1].CanonicalName.Should().Be("(Not Normalized)");

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescriptionAsync_FiltersByDateRange()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid normalizedId = Guid.NewGuid();

		Guid receiptInRange = Guid.NewGuid();
		Guid receiptOutOfRange = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Bananas",
				Status = Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});

			context.Receipts.AddRange(
				new ReceiptEntity
				{
					Id = receiptInRange,
					Location = "Store",
					Date = new DateOnly(2025, 6, 15),
					TaxAmount = 0m,
				},
				new ReceiptEntity
				{
					Id = receiptOutOfRange,
					Location = "Store",
					Date = new DateOnly(2024, 1, 1),
					TaxAmount = 0m,
				});

			context.ReceiptItems.AddRange(
				new ReceiptItemEntity
				{
					Id = Guid.NewGuid(),
					ReceiptId = receiptInRange,
					Description = "bananas",
					Quantity = 1,
					UnitPrice = 1.50m,
					TotalAmount = 1.50m,
					Category = "Produce",
					NormalizedDescriptionId = normalizedId,
				},
				new ReceiptItemEntity
				{
					Id = Guid.NewGuid(),
					ReceiptId = receiptOutOfRange,
					Description = "bananas",
					Quantity = 1,
					UnitPrice = 99.99m,
					TotalAmount = 99.99m,
					Category = "Produce",
					NormalizedDescriptionId = normalizedId,
				});

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		DateTimeOffset from = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
		DateTimeOffset to = new(2025, 12, 31, 0, 0, 0, TimeSpan.Zero);

		// Act
		SpendingByNormalizedDescriptionResult result = await service
			.GetSpendingByNormalizedDescriptionAsync(from, to, "totalAmount", "desc", 1, 50, CancellationToken.None);

		// Assert — only the receipt in range contributed
		result.Items.Should().ContainSingle();
		result.Items[0].TotalAmount.Should().Be(1.50m);
		result.Items[0].ItemCount.Should().Be(1);
		result.FromDate.Should().Be(from);
		result.ToDate.Should().Be(to);
		result.TotalCount.Should().Be(1);
		result.GrandTotal.Should().Be(1.50m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescriptionAsync_IgnoresSoftDeletedItemsAndReceipts()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid normalizedId = Guid.NewGuid();
		Guid liveReceipt = Guid.NewGuid();
		Guid deletedReceipt = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Eggs",
				Status = Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});

			context.Receipts.AddRange(
				new ReceiptEntity
				{
					Id = liveReceipt,
					Location = "Store",
					Date = new DateOnly(2025, 3, 1),
					TaxAmount = 0m,
				},
				new ReceiptEntity
				{
					Id = deletedReceipt,
					Location = "Store",
					Date = new DateOnly(2025, 3, 1),
					TaxAmount = 0m,
					DeletedAt = DateTimeOffset.UtcNow,
				});

			context.ReceiptItems.AddRange(
				// Live item on live receipt — counted
				new ReceiptItemEntity
				{
					Id = Guid.NewGuid(),
					ReceiptId = liveReceipt,
					Description = "eggs",
					Quantity = 1,
					UnitPrice = 3.00m,
					TotalAmount = 3.00m,
					Category = "Dairy",
					NormalizedDescriptionId = normalizedId,
				},
				// Soft-deleted item on live receipt — excluded
				new ReceiptItemEntity
				{
					Id = Guid.NewGuid(),
					ReceiptId = liveReceipt,
					Description = "eggs",
					Quantity = 1,
					UnitPrice = 100.00m,
					TotalAmount = 100.00m,
					Category = "Dairy",
					NormalizedDescriptionId = normalizedId,
					DeletedAt = DateTimeOffset.UtcNow,
				},
				// Live item on deleted receipt — excluded (because receipt is deleted)
				new ReceiptItemEntity
				{
					Id = Guid.NewGuid(),
					ReceiptId = deletedReceipt,
					Description = "eggs",
					Quantity = 1,
					UnitPrice = 50.00m,
					TotalAmount = 50.00m,
					Category = "Dairy",
					NormalizedDescriptionId = normalizedId,
				});

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		SpendingByNormalizedDescriptionResult result = await service
			.GetSpendingByNormalizedDescriptionAsync(null, null, "totalAmount", "desc", 1, 50, CancellationToken.None);

		// Assert — only the live item on the live receipt survives
		result.Items.Should().ContainSingle();
		result.Items[0].CanonicalName.Should().Be("Eggs");
		result.Items[0].TotalAmount.Should().Be(3.00m);
		result.Items[0].ItemCount.Should().Be(1);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescriptionAsync_ReturnsEmpty_WhenNoItems()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		ReportService service = new(contextFactory);

		// Act
		SpendingByNormalizedDescriptionResult result = await service
			.GetSpendingByNormalizedDescriptionAsync(null, null, "totalAmount", "desc", 1, 50, CancellationToken.None);

		// Assert
		result.Items.Should().BeEmpty();
		result.FromDate.Should().BeNull();
		result.ToDate.Should().BeNull();
		result.TotalCount.Should().Be(0);
		result.GrandTotal.Should().Be(0m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescriptionAsync_UsesFirstAndLastSeenDates()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid normalizedId = Guid.NewGuid();
		Guid r1 = Guid.NewGuid();
		Guid r2 = Guid.NewGuid();
		Guid r3 = Guid.NewGuid();

		DateOnly day1 = new(2025, 1, 5);
		DateOnly day2 = new(2025, 6, 20);
		DateOnly day3 = new(2025, 11, 30);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Coffee",
				Status = Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});

			context.Receipts.AddRange(
				new ReceiptEntity { Id = r1, Location = "S", Date = day1, TaxAmount = 0m },
				new ReceiptEntity { Id = r2, Location = "S", Date = day2, TaxAmount = 0m },
				new ReceiptEntity { Id = r3, Location = "S", Date = day3, TaxAmount = 0m });

			context.ReceiptItems.AddRange(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = r1, Description = "coffee", Quantity = 1, UnitPrice = 4m, TotalAmount = 4m, Category = "Beverages", NormalizedDescriptionId = normalizedId },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = r2, Description = "coffee", Quantity = 1, UnitPrice = 4m, TotalAmount = 4m, Category = "Beverages", NormalizedDescriptionId = normalizedId },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = r3, Description = "coffee", Quantity = 1, UnitPrice = 4m, TotalAmount = 4m, Category = "Beverages", NormalizedDescriptionId = normalizedId });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		SpendingByNormalizedDescriptionResult result = await service
			.GetSpendingByNormalizedDescriptionAsync(null, null, "totalAmount", "desc", 1, 50, CancellationToken.None);

		// Assert
		result.Items.Should().ContainSingle();
		SpendingByNormalizedDescriptionItem bucket = result.Items[0];
		bucket.ItemCount.Should().Be(3);
		bucket.FirstSeen.Should().Be(new DateTimeOffset(day1.Year, day1.Month, day1.Day, 0, 0, 0, TimeSpan.Zero));
		bucket.LastSeen.Should().Be(new DateTimeOffset(day3.Year, day3.Month, day3.Day, 0, 0, 0, TimeSpan.Zero));

		contextFactory.ResetDatabase();
	}

	// ── GetUncategorizedItemsAsync (RECEIPTS-791: paginate/count/sort in SQL) ─────────
	// These assert the Count + Skip/Take + ORDER-BY semantics are preserved after moving the
	// work off the client and into the query. They run against the InMemory provider, which
	// evaluates LINQ in-process, so they prove the query LOGIC — not that the expressions
	// translate to SQL. True SQL-translation proof requires an integration test on PostgreSQL.

	[Fact]
	public async Task GetUncategorizedItemsAsync_CountsPaginatesAndSortsByDescription()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid receiptId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2025, 3, 1), TaxAmount = 0m });

			context.ReceiptItems.AddRange(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Cabbage", Quantity = 1, UnitPrice = 3m, TotalAmount = 3m, Category = "Uncategorized" },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Apple", Quantity = 1, UnitPrice = 1m, TotalAmount = 1m, Category = "Uncategorized" },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Bread", Quantity = 1, UnitPrice = 2m, TotalAmount = 2m, Category = "Uncategorized" },
				// Categorized — excluded by the Category == "Uncategorized" filter.
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Milk", Quantity = 1, UnitPrice = 4m, TotalAmount = 4m, Category = "Dairy" },
				// Soft-deleted uncategorized — excluded by the DeletedAt == null filter.
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Ghost", Quantity = 1, UnitPrice = 9m, TotalAmount = 9m, Category = "Uncategorized", DeletedAt = DateTimeOffset.UtcNow });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act — page 1 of 2, default sort (description asc).
		UncategorizedItemsResult page1 = await service.GetUncategorizedItemsAsync("description", "asc", 1, 2, CancellationToken.None);

		// Assert — count reflects only the 3 active uncategorized items; the page holds the first
		// two by description ascending (Apple, Bread).
		page1.TotalCount.Should().Be(3);
		page1.Items.Should().HaveCount(2);
		page1.Items.Select(i => i.Description).Should().ContainInOrder("Apple", "Bread");

		// Act — page 2 carries the remaining item.
		UncategorizedItemsResult page2 = await service.GetUncategorizedItemsAsync("description", "asc", 2, 2, CancellationToken.None);

		// Assert
		page2.TotalCount.Should().Be(3);
		page2.Items.Should().ContainSingle();
		page2.Items[0].Description.Should().Be("Cabbage");

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetUncategorizedItemsAsync_SortsByTotalDescending()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid receiptId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2025, 3, 1), TaxAmount = 0m });

			context.ReceiptItems.AddRange(
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Low", Quantity = 1, UnitPrice = 1m, TotalAmount = 1m, Category = "Uncategorized" },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "High", Quantity = 1, UnitPrice = 9m, TotalAmount = 9m, Category = "Uncategorized" },
				new ReceiptItemEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, Description = "Mid", Quantity = 1, UnitPrice = 5m, TotalAmount = 5m, Category = "Uncategorized" });

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		UncategorizedItemsResult result = await service.GetUncategorizedItemsAsync("total", "desc", 1, 50, CancellationToken.None);

		// Assert — ordered by TotalAmount descending.
		result.TotalCount.Should().Be(3);
		result.Items.Select(i => i.TotalAmount).Should().ContainInOrder(9m, 5m, 1m);

		contextFactory.ResetDatabase();
	}

	// ── GetSpendingByLocationAsync (RECEIPTS-791: aggregate/count/paginate in SQL) ────

	[Fact]
	public async Task GetSpendingByLocationAsync_AggregatesCountsPaginatesAndSortsByTotal()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// Store A: two visits totaling 30; Store B: one visit of 20; Store C: one visit of 10.
			AddReceiptWithTransaction(context, "Store A", new DateOnly(2025, 1, 1), accountId, 10m);
			AddReceiptWithTransaction(context, "Store A", new DateOnly(2025, 1, 2), accountId, 20m);
			AddReceiptWithTransaction(context, "Store B", new DateOnly(2025, 1, 3), accountId, 20m);
			AddReceiptWithTransaction(context, "Store C", new DateOnly(2025, 1, 4), accountId, 10m);

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act — default sort (total desc), page 1 of 2.
		SpendingByLocationResult page1 = await service.GetSpendingByLocationAsync(null, null, "total", "desc", 1, 2, CancellationToken.None);

		// Assert — 3 location groups; grand total sums every transaction (60); the page holds the
		// two highest-total locations in order, with Store A's two visits aggregated.
		page1.TotalCount.Should().Be(3);
		page1.GrandTotal.Should().Be(60m);
		page1.Items.Should().HaveCount(2);
		page1.Items.Select(i => i.Location).Should().ContainInOrder("Store A", "Store B");
		page1.Items[0].Total.Should().Be(30m);
		page1.Items[0].Visits.Should().Be(2);
		page1.Items[0].AveragePerVisit.Should().Be(15m);

		// Act — page 2 carries the lowest-total location.
		SpendingByLocationResult page2 = await service.GetSpendingByLocationAsync(null, null, "total", "desc", 2, 2, CancellationToken.None);

		// Assert
		page2.TotalCount.Should().Be(3);
		page2.Items.Should().ContainSingle();
		page2.Items[0].Location.Should().Be("Store C");

		contextFactory.ResetDatabase();
	}

	// ── Deterministic pagination when the primary sort key ties (RECEIPTS-791 follow-up) ──
	// Mirrors the ApplySort determinism tests (RECEIPTS-768): when every row shares the primary
	// sort value, only the unique-key tiebreaker gives a stable total order, so walking the pages
	// must cover every row exactly once (no gap, no duplicate) in a repeatable order.

	[Fact]
	public async Task GetUncategorizedItemsAsync_TiedTotal_PaginatesWithoutGapsOrDuplicates()
	{
		// Arrange — five uncategorized items that all TIE on the primary sort key (TotalAmount).
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid receiptId = Guid.NewGuid();

		List<Guid> seededIds = [];
		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			context.Receipts.Add(
				new ReceiptEntity { Id = receiptId, Location = "Store", Date = new DateOnly(2025, 3, 1), TaxAmount = 0m });

			for (int i = 0; i < 5; i++)
			{
				Guid id = Guid.NewGuid();
				seededIds.Add(id);
				context.ReceiptItems.Add(new ReceiptItemEntity
				{
					Id = id,
					ReceiptId = receiptId,
					Description = $"Item {i}",
					Quantity = 1,
					UnitPrice = 5m,
					TotalAmount = 5m, // identical => primary sort ties for every row
					Category = "Uncategorized",
				});
			}

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act — walk every page (size 2) with a tied primary sort.
		List<Guid> pagedIds = [];
		for (int page = 1; page <= 3; page++)
		{
			UncategorizedItemsResult result =
				await service.GetUncategorizedItemsAsync("total", "desc", page, 2, CancellationToken.None);
			result.TotalCount.Should().Be(5);
			pagedIds.AddRange(result.Items.Select(i => i.Id));
		}

		// Assert — every row appears exactly once, ordered by the ascending-Id tiebreaker.
		pagedIds.Should().HaveCount(5);
		pagedIds.Should().OnlyHaveUniqueItems();
		pagedIds.Should().Equal(seededIds.OrderBy(id => id));

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetOutOfBalanceAsync_TiedDateAndDifference_PaginatesWithoutGapsOrDuplicates()
	{
		// Arrange — five receipts identical in Date AND out-of-balance Difference, so both primary
		// sort keys tie and only the unique receipt-Id tiebreaker orders them.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly sameDate = new(2025, 3, 1);

		List<Guid> seededIds = [];
		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			for (int i = 0; i < 5; i++)
			{
				Guid receiptId = Guid.NewGuid();
				seededIds.Add(receiptId);
				// expected total = 0, transaction = 50 => difference = -50 for every receipt.
				context.Receipts.Add(
					new ReceiptEntity { Id = receiptId, Location = $"Store {i}", Date = sameDate, TaxAmount = 0m });
				context.Transactions.Add(
					new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, AccountId = accountId, Amount = 50m, Date = sameDate });
			}

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act — walk every page (size 2) with the default tied date sort.
		List<Guid> pagedIds = [];
		for (int page = 1; page <= 3; page++)
		{
			OutOfBalanceResult result =
				await service.GetOutOfBalanceAsync("date", "asc", page, 2, CancellationToken.None);
			result.TotalCount.Should().Be(5);
			pagedIds.AddRange(result.Items.Select(i => i.ReceiptId));
		}

		// Assert — every receipt appears exactly once, ordered by the ascending receipt-Id tiebreaker.
		pagedIds.Should().HaveCount(5);
		pagedIds.Should().OnlyHaveUniqueItems();
		pagedIds.Should().Equal(seededIds.OrderBy(id => id));

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetSpendingByLocationAsync_TiedTotal_PaginatesWithoutGapsOrDuplicates()
	{
		// Arrange — five distinct locations, each a single visit of the SAME total, so the "total"
		// measure ties for every group and only the unique Location tiebreaker orders them.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		List<string> locations = ["Alpha", "Bravo", "Charlie", "Delta", "Echo"];

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			DateOnly date = new(2025, 1, 1);
			foreach (string loc in locations)
			{
				AddReceiptWithTransaction(context, loc, date, accountId, 10m);
			}

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act — walk every page (size 2) with a tied total-desc sort.
		List<string> pagedLocations = [];
		for (int page = 1; page <= 3; page++)
		{
			SpendingByLocationResult result =
				await service.GetSpendingByLocationAsync(null, null, "total", "desc", page, 2, CancellationToken.None);
			result.TotalCount.Should().Be(5);
			pagedLocations.AddRange(result.Items.Select(i => i.Location));
		}

		// Assert — every location appears exactly once, ordered by the ascending Location tiebreaker.
		pagedLocations.Should().HaveCount(5);
		pagedLocations.Should().OnlyHaveUniqueItems();
		pagedLocations.Should().Equal(locations.OrderBy(l => l));

		contextFactory.ResetDatabase();
	}

	private static void AddReceiptWithTransaction(
		ApplicationDbContext context, string location, DateOnly date, Guid accountId, decimal amount)
	{
		Guid receiptId = Guid.NewGuid();
		context.Receipts.Add(
			new ReceiptEntity { Id = receiptId, Location = location, Date = date, TaxAmount = 0m });
		context.Transactions.Add(
			new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, AccountId = accountId, Amount = amount, Date = date });
	}

	// ── GetDuplicatesAsync + duplicate-group acceptance (RECEIPTS-834) ────────────────
	// The InMemory provider enforces neither the canonical-order check constraint nor the
	// filtered unique index, so these prove the SERVICE contract: which groups are computed,
	// which are suppressed, and how the pairwise acceptance rows evolve across edits.

	[Fact]
	public async Task GetDuplicatesAsync_DateAndLocation_GroupsSameDaySameLocation_AndSkipsLoneReceipt()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		Guid lone = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			// Same day, different location — no partner, so no group.
			SeedReceipt(context, lone, "Store B", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		DuplicateDetectionResult result = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: false, CancellationToken.None);

		// Assert
		result.Groups.Should().ContainSingle();
		result.GroupCount.Should().Be(1);
		result.TotalDuplicateReceipts.Should().Be(2);
		result.Groups[0].MatchKey.Should().Be("2025-05-01 @ Store A");
		result.Groups[0].IsAccepted.Should().BeFalse();
		result.Groups[0].Receipts.Select(r => r.ReceiptId).Should()
			.BeEquivalentTo(new[] { receiptA, receiptB });

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetDuplicatesAsync_AcceptedGroup_IsSuppressed_AndStaysSuppressedOnRepeatCalls()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// The group is reported before acceptance.
		DuplicateDetectionResult before = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: false, CancellationToken.None);
		before.Groups.Should().ContainSingle();

		// Act
		int acceptedPairs = await service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);

		// Assert — gone from the default report, and it stays gone on a second call.
		acceptedPairs.Should().Be(1);

		DuplicateDetectionResult first = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: false, CancellationToken.None);
		first.Groups.Should().BeEmpty();
		first.GroupCount.Should().Be(0);
		first.TotalDuplicateReceipts.Should().Be(0);

		DuplicateDetectionResult second = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: false, CancellationToken.None);
		second.Groups.Should().BeEmpty();

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetDuplicatesAsync_IncludeAccepted_ReturnsAcceptedGroupFlagged_AndLeavesOthersUnflagged()
	{
		// Arrange — two independent groups; only the first is accepted.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly acceptedDate = new(2025, 5, 1);
		DateOnly openDate = new(2025, 6, 1);

		Guid acceptedA = Guid.NewGuid();
		Guid acceptedB = Guid.NewGuid();
		Guid openA = Guid.NewGuid();
		Guid openB = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, acceptedA, "Store A", acceptedDate, accountId, 10.00m);
			SeedReceipt(context, acceptedB, "Store A", acceptedDate, accountId, 10.00m);
			SeedReceipt(context, openA, "Store B", openDate, accountId, 20.00m);
			SeedReceipt(context, openB, "Store B", openDate, accountId, 20.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);
		await service.AcceptDuplicateGroupAsync([acceptedA, acceptedB], CancellationToken.None);

		// Act
		DuplicateDetectionResult result = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: true, CancellationToken.None);

		// Assert — both groups present, distinguished by the IsAccepted flag.
		result.Groups.Should().HaveCount(2);
		result.GroupCount.Should().Be(2);
		result.TotalDuplicateReceipts.Should().Be(4);

		DuplicateGroup accepted = result.Groups.Single(g => g.Receipts.Any(r => r.ReceiptId == acceptedA));
		accepted.IsAccepted.Should().BeTrue();

		DuplicateGroup open = result.Groups.Single(g => g.Receipts.Any(r => r.ReceiptId == openA));
		open.IsAccepted.Should().BeFalse();

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetDuplicatesAsync_AcceptanceSurvivesMatchOnAndToleranceChanges()
	{
		// Arrange — acceptance is keyed on receipt identity, never on the MatchKey, so changing
		// matchOn / locationTolerance / totalTolerance must not resurrect the dismissal
		// (RECEIPTS-834). Every mode below produces a DIFFERENT MatchKey for the same two receipts.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		(string MatchOn, string LocationTolerance, decimal TotalTolerance)[] settings =
		[
			("dateAndLocationAndTotal", "exact", 0m),
			("dateAndLocation", "exact", 0m),
			("dateAndTotal", "exact", 1m),
			("dateAndLocationAndTotal", "normalized", 5m),
		];

		// Every setting combination clusters these two receipts BEFORE acceptance — otherwise the
		// suppression assertions below would pass vacuously. Each produces a different MatchKey.
		List<string> matchKeys = [];
		foreach ((string matchOn, string locationTolerance, decimal totalTolerance) in settings)
		{
			DuplicateDetectionResult baseline = await service.GetDuplicatesAsync(
				matchOn, locationTolerance, totalTolerance, includeAccepted: false, CancellationToken.None);
			baseline.Groups.Should().ContainSingle();
			matchKeys.Add(baseline.Groups[0].MatchKey);
		}

		matchKeys.Distinct().Should().HaveCountGreaterThan(1);

		// Act — accept under the strictest settings only.
		await service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);

		// Assert — the same receipt set stays suppressed under every other setting combination.
		foreach ((string matchOn, string locationTolerance, decimal totalTolerance) in settings)
		{
			DuplicateDetectionResult suppressed = await service.GetDuplicatesAsync(
				matchOn, locationTolerance, totalTolerance, includeAccepted: false, CancellationToken.None);
			suppressed.Groups.Should().BeEmpty($"{matchOn}/{locationTolerance}/{totalTolerance} must honour the acceptance");

			DuplicateDetectionResult flagged = await service.GetDuplicatesAsync(
				matchOn, locationTolerance, totalTolerance, includeAccepted: true, CancellationToken.None);
			flagged.Groups.Should().ContainSingle();
			flagged.Groups[0].IsAccepted.Should().BeTrue();
			flagged.Groups[0].Receipts.Select(r => r.ReceiptId).Should()
				.BeEquivalentTo(new[] { receiptA, receiptB });
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetDuplicatesAsync_GroupThatGainsAMember_IsReportedAgain()
	{
		// Arrange — accept {A,B}, then a third receipt joins the same date+location key.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		Guid receiptC = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);
		await service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);

		// Act — the newcomer's pairs have never been reviewed, so the group resurfaces.
		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptC, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		DuplicateDetectionResult result = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: false, CancellationToken.None);

		// Assert
		result.Groups.Should().ContainSingle();
		result.Groups[0].IsAccepted.Should().BeFalse();
		result.Groups[0].Receipts.Select(r => r.ReceiptId).Should()
			.BeEquivalentTo(new[] { receiptA, receiptB, receiptC });

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetDuplicatesAsync_GroupThatLosesAMember_StaysSuppressed()
	{
		// Arrange — accept {A,B,C}, then soft-delete C. The remaining pair {A,B} is still fully
		// accepted, so the shrunken group must stay quiet.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		Guid receiptC = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptC, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);
		int acceptedPairs = await service.AcceptDuplicateGroupAsync(
			[receiptA, receiptB, receiptC], CancellationToken.None);
		acceptedPairs.Should().Be(3);

		// Act — soft-delete C (the context converts the remove into a soft delete).
		await SoftDeleteReceiptAsync(contextFactory, receiptC);

		DuplicateDetectionResult result = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: false, CancellationToken.None);

		// Assert
		result.Groups.Should().BeEmpty();

		// And with includeAccepted the surviving pair is still flagged as accepted.
		DuplicateDetectionResult withAccepted = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: true, CancellationToken.None);
		withAccepted.Groups.Should().ContainSingle();
		withAccepted.Groups[0].IsAccepted.Should().BeTrue();
		withAccepted.Groups[0].Receipts.Select(r => r.ReceiptId).Should()
			.BeEquivalentTo(new[] { receiptA, receiptB });

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetDuplicatesAsync_SoftDeleteThenRestore_KeepsGroupSuppressed()
	{
		// Arrange — acceptance is NOT cascaded away when a member receipt is soft-deleted, so
		// restoring that receipt must bring back a still-suppressed group.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);
		await service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);

		// Act — soft-delete B: the group drops below two members and is not reported at all.
		await SoftDeleteReceiptAsync(contextFactory, receiptB);

		DuplicateDetectionResult whileDeleted = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: true, CancellationToken.None);
		whileDeleted.Groups.Should().BeEmpty();

		// Act — restore B.
		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			ReceiptEntity restored = await context.Receipts
				.IgnoreQueryFilters()
				.SingleAsync(r => r.Id == receiptB);
			restored.DeletedAt = null;
			await context.SaveChangesAsync();
		}

		// Assert — the group reforms but is STILL accepted, so the default report stays clean.
		DuplicateDetectionResult afterRestore = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: false, CancellationToken.None);
		afterRestore.Groups.Should().BeEmpty();

		DuplicateDetectionResult afterRestoreIncludingAccepted = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: true, CancellationToken.None);
		afterRestoreIncludingAccepted.Groups.Should().ContainSingle();
		afterRestoreIncludingAccepted.Groups[0].IsAccepted.Should().BeTrue();

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task AcceptDuplicateGroupAsync_IsIdempotent()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		int firstCall = await service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);
		int secondCall = await service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);

		// Assert — the second call adds nothing and does not duplicate the stored pair.
		firstCall.Should().Be(1);
		secondCall.Should().Be(0);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			(await context.AcceptedDuplicatePairs.CountAsync()).Should().Be(1);
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task AcceptDuplicateGroupAsync_ThrowsKeyNotFound_WhenReceiptDoesNotExist()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid ghost = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		Func<Task> act = () => service.AcceptDuplicateGroupAsync([receiptA, ghost], CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<KeyNotFoundException>()
			.WithMessage($"*{ghost}*");

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task AcceptDuplicateGroupAsync_ManyMissingReceipts_TruncatesTheNotFoundMessage()
	{
		// Arrange — the message is returned verbatim as a 404 body AND logged, so an unbounded join
		// over every unmatched GUID turns a bad request into a multi-megabyte response and log line.
		// A reviewer measured 50,000 GUIDs producing a 1.9 MB message.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		Guid receiptA = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", new DateOnly(2025, 6, 1), accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);
		List<Guid> ghosts = [.. Enumerable.Range(0, 25).Select(_ => Guid.NewGuid())];

		// Act
		Func<Task> act = () => service.AcceptDuplicateGroupAsync([receiptA, .. ghosts], CancellationToken.None);

		// Assert — 10 enumerated, the rest collapsed into a count.
		KeyNotFoundException thrown = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which;
		thrown.Message.Should().Contain("(+15 more)");

		int enumerated = ghosts.Count(id => thrown.Message.Contains(id.ToString(), StringComparison.Ordinal));
		enumerated.Should().Be(10, "only the first 10 missing IDs are echoed back");

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task AcceptDuplicateGroupAsync_ThrowsKeyNotFound_WhenReceiptIsSoftDeleted()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		await SoftDeleteReceiptAsync(contextFactory, receiptB);

		ReportService service = new(contextFactory);

		// Act
		Func<Task> act = () => service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<KeyNotFoundException>()
			.WithMessage($"*{receiptB}*");

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task AcceptDuplicateGroupAsync_ReturnsZero_WhenFewerThanTwoDistinctIds()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		int single = await service.AcceptDuplicateGroupAsync([receiptA], CancellationToken.None);
		int none = await service.AcceptDuplicateGroupAsync([], CancellationToken.None);
		int repeated = await service.AcceptDuplicateGroupAsync([receiptA, receiptA], CancellationToken.None);

		// Assert — the id list collapses to fewer than two distinct receipts, so nothing is stored.
		single.Should().Be(0);
		none.Should().Be(0);
		repeated.Should().Be(0);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			(await context.AcceptedDuplicatePairs.CountAsync()).Should().Be(0);
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task AcceptDuplicateGroupAsync_ThreeReceipts_StoresEveryUnorderedPairInCanonicalOrder()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		Guid receiptC = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptC, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		int accepted = await service.AcceptDuplicateGroupAsync(
			[receiptA, receiptB, receiptC], CancellationToken.None);

		// Assert — C(3,2) = 3 rows, each stored with the lower GUID in ReceiptIdA.
		accepted.Should().Be(3);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<AcceptedDuplicatePairEntity> pairs = await context.AcceptedDuplicatePairs.ToListAsync();
			pairs.Should().HaveCount(3);
			pairs.Should().OnlyContain(p => p.ReceiptIdA.CompareTo(p.ReceiptIdB) < 0);
			pairs.Select(p => (p.ReceiptIdA, p.ReceiptIdB)).Should().OnlyHaveUniqueItems();
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task UnacceptDuplicateGroupAsync_RemovesAcceptance_AndGroupIsReportedAgain()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		Guid receiptC = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptC, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);
		await service.AcceptDuplicateGroupAsync([receiptA, receiptB, receiptC], CancellationToken.None);

		// Act
		int removed = await service.UnacceptDuplicateGroupAsync(
			[receiptA, receiptB, receiptC], CancellationToken.None);

		// Assert — all three pairs removed and the group is visible again.
		removed.Should().Be(3);

		DuplicateDetectionResult result = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: false, CancellationToken.None);
		result.Groups.Should().ContainSingle();
		result.Groups[0].IsAccepted.Should().BeFalse();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// Soft-deleted, not hard-deleted: the tombstones survive for the audit trail.
			(await context.AcceptedDuplicatePairs.CountAsync()).Should().Be(0);
			(await context.AcceptedDuplicatePairs.IgnoreQueryFilters().CountAsync()).Should().Be(3);
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task UnacceptDuplicateGroupAsync_ReturnsZero_WhenNothingWasAccepted()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act
		int removedWithNothingStored = await service.UnacceptDuplicateGroupAsync(
			[receiptA, receiptB], CancellationToken.None);
		int removedWithTooFewIds = await service.UnacceptDuplicateGroupAsync(
			[receiptA, receiptA], CancellationToken.None);

		// Assert
		removedWithNothingStored.Should().Be(0);
		removedWithTooFewIds.Should().Be(0);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task AcceptDuplicateGroupAsync_AfterUnaccept_LeavesExactlyOneActiveRow_AndKeepsTheTombstone()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Act — accept, un-accept, accept again.
		await service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);
		await service.UnacceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);
		int reAccepted = await service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);

		// Assert — exactly one ACTIVE row, which is what the filtered unique index guards. The
		// un-accept's tombstone is deliberately left in place rather than resurrected: the partial
		// index does not cover tombstones, so the Postgres ON CONFLICT path cannot see them, and
		// keeping the row preserves the fact that the pair was once un-accepted.
		reAccepted.Should().Be(1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			(await context.AcceptedDuplicatePairs.CountAsync()).Should().Be(1);
			(await context.AcceptedDuplicatePairs.IgnoreQueryFilters().CountAsync()).Should().Be(2);

			AcceptedDuplicatePairEntity active = await context.AcceptedDuplicatePairs.SingleAsync();
			active.DeletedAt.Should().BeNull();
			active.DeletedByUserId.Should().BeNull();
			active.CascadeDeletedByParentId.Should().BeNull();
		}

		DuplicateDetectionResult result = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: false, CancellationToken.None);
		result.Groups.Should().BeEmpty();

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAcceptedDuplicatesAsync_ReturnsAcceptedGroupWithHydratedReceipts()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly earlier = new(2025, 5, 1);
		DateOnly later = new(2025, 5, 2);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", earlier, accountId, 12.34m);
			SeedReceipt(context, receiptB, "Store B", later, accountId, 56.78m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);
		DateTimeOffset beforeAccept = DateTimeOffset.UtcNow.AddSeconds(-1);
		await service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);

		// Act
		AcceptedDuplicatesResult result = await service.GetAcceptedDuplicatesAsync(CancellationToken.None);

		// Assert
		result.Groups.Should().ContainSingle();
		result.GroupCount.Should().Be(1);

		AcceptedDuplicateGroup group = result.Groups[0];
		group.AcceptedAt.Should().BeOnOrAfter(beforeAccept);
		group.Receipts.Should().HaveCount(2);

		// Ordered by date, so the earlier receipt comes first, fully hydrated.
		group.Receipts[0].ReceiptId.Should().Be(receiptA);
		group.Receipts[0].Location.Should().Be("Store A");
		group.Receipts[0].Date.Should().Be(earlier);
		group.Receipts[0].TransactionTotal.Should().Be(12.34m);
		group.Receipts[1].ReceiptId.Should().Be(receiptB);
		group.Receipts[1].TransactionTotal.Should().Be(56.78m);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAcceptedDuplicatesAsync_ReturnsEmpty_WhenNothingAccepted()
	{
		// Arrange
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		ReportService service = new(contextFactory);

		// Act
		AcceptedDuplicatesResult result = await service.GetAcceptedDuplicatesAsync(CancellationToken.None);

		// Assert
		result.Groups.Should().BeEmpty();
		result.GroupCount.Should().Be(0);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAcceptedDuplicatesAsync_OmitsComponentWithFewerThanTwoActiveReceipts()
	{
		// Arrange — a two-receipt acceptance whose partner is soft-deleted can never produce a
		// duplicate warning again, so it is not listed (the acceptance row itself is untouched).
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);
		await service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);

		// Act
		await SoftDeleteReceiptAsync(contextFactory, receiptB);
		AcceptedDuplicatesResult result = await service.GetAcceptedDuplicatesAsync(CancellationToken.None);

		// Assert
		result.Groups.Should().BeEmpty();
		result.GroupCount.Should().Be(0);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			// The acceptance survives the soft delete, which is what lets a restore stay quiet.
			(await context.AcceptedDuplicatePairs.CountAsync()).Should().Be(1);
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAcceptedDuplicatesAsync_MergesAcceptancesSharingAReceiptIntoOneComponent()
	{
		// Arrange — {A,B} and {B,C} are separate acceptances that share B. The pairwise model
		// surfaces them as ONE connected component; this is the documented tradeoff.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 5, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		Guid receiptC = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store B", date, accountId, 20.00m);
			SeedReceipt(context, receiptC, "Store C", date, accountId, 30.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);
		await service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);
		await service.AcceptDuplicateGroupAsync([receiptB, receiptC], CancellationToken.None);

		// Act
		AcceptedDuplicatesResult result = await service.GetAcceptedDuplicatesAsync(CancellationToken.None);

		// Assert — one group holding all three receipts, even though {A,C} was never accepted.
		result.Groups.Should().ContainSingle();
		result.GroupCount.Should().Be(1);
		result.Groups[0].Receipts.Select(r => r.ReceiptId).Should()
			.BeEquivalentTo(new[] { receiptA, receiptB, receiptC });

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAcceptedDuplicatesAsync_ReportsEveryMember_EvenWhenOneIsSoftDeleted()
	{
		// Arrange — accept {A,B,C}, then soft-delete C. The displayed list drops C, so a client that
		// submitted only what it could render would strand (A,C) and (B,C) with no producible set able
		// to reach them, leaving Undo permanently dead. MemberReceiptIds is what prevents that: it
		// carries every member so the client can always submit the complete set.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 6, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		Guid receiptC = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptC, "Store A", date, accountId, 10.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);
		(await service.AcceptDuplicateGroupAsync([receiptA, receiptB, receiptC], CancellationToken.None))
			.Should().Be(3);

		await SoftDeleteReceiptAsync(contextFactory, receiptC);

		// Act
		AcceptedDuplicatesResult accepted = await service.GetAcceptedDuplicatesAsync(CancellationToken.None);

		// Assert — C is absent from the display list but present in the member list.
		accepted.Groups.Should().ContainSingle();
		accepted.Groups[0].Receipts.Select(r => r.ReceiptId)
			.Should().BeEquivalentTo([receiptA, receiptB], "the deleted receipt has nothing to display");
		accepted.Groups[0].MemberReceiptIds
			.Should().BeEquivalentTo([receiptA, receiptB, receiptC], "undo needs every member");

		// Submitting that member list clears the whole acceptance, orphans included.
		int removed = await service.UnacceptDuplicateGroupAsync(
			accepted.Groups[0].MemberReceiptIds, CancellationToken.None);
		removed.Should().Be(3);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			(await context.AcceptedDuplicatePairs.CountAsync()).Should().Be(0);
		}

		// And the surviving pair is genuinely reported again.
		DuplicateDetectionResult result = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: false, CancellationToken.None);
		result.Groups.Should().ContainSingle();
		result.Groups[0].Receipts.Select(r => r.ReceiptId).Should().BeEquivalentTo([receiptA, receiptB]);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task UnacceptDuplicateGroupAsync_ClusterSubsetOfAWiderAcceptance_LeavesNeighbouringPairsIntact()
	{
		// Arrange — the regression that killed component expansion.
		//
		// "Report again" in the report acts on a CLUSTER, which can be a strict subset of the
		// acceptance component. Accept {A,B} and {C,D} at tolerance 0; widen tolerance so all four
		// cluster together and accept that too; narrow tolerance again and the report shows {A,B} and
		// {C,D} separately. Clicking "Report again" on {A,B} must not touch the {C,D} acceptance.
		// Expanding to the connected component destroyed it. Every tolerance here is a dropdown value.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 6, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		Guid receiptC = Guid.NewGuid();
		Guid receiptD = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptC, "Store A", date, accountId, 10.50m);
			SeedReceipt(context, receiptD, "Store A", date, accountId, 10.50m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Tolerance 0 clusters {A,B} and {C,D}; accept both.
		await service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);
		await service.AcceptDuplicateGroupAsync([receiptC, receiptD], CancellationToken.None);

		// Tolerance 1.00 clusters all four; accept that too. The graph is now fully connected.
		await service.AcceptDuplicateGroupAsync(
			[receiptA, receiptB, receiptC, receiptD], CancellationToken.None);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			(await context.AcceptedDuplicatePairs.CountAsync()).Should().Be(6);
		}

		// Act — back at tolerance 0, "Report again" on the {A,B} cluster only.
		int removed = await service.UnacceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);

		// Assert — only (A,B) went. The user's separate {C,D} acceptance survives.
		removed.Should().Be(1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			bool cdStillAccepted = await context.AcceptedDuplicatePairs.AnyAsync(p =>
				(p.ReceiptIdA == receiptC && p.ReceiptIdB == receiptD)
				|| (p.ReceiptIdA == receiptD && p.ReceiptIdB == receiptC));
			cdStillAccepted.Should().BeTrue("un-accepting {A,B} must not destroy the {C,D} acceptance");

			(await context.AcceptedDuplicatePairs.CountAsync()).Should().Be(5);
		}

		// {A,B} is reported again; {C,D} stays suppressed.
		DuplicateDetectionResult result = await service.GetDuplicatesAsync(
			"dateAndTotal", "exact", 0m, includeAccepted: false, CancellationToken.None);
		result.Groups.Should().ContainSingle();
		result.Groups[0].Receipts.Select(r => r.ReceiptId).Should().BeEquivalentTo([receiptA, receiptB]);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task UnacceptDuplicateGroupAsync_LeavesAcceptancesThatShareNoReceipt_Untouched()
	{
		// Arrange — this pins the predicate. Unaccept removes exactly the pairs whose BOTH ends were
		// submitted. Widening the match to "either end" would destroy every acceptance involving A or
		// B with any unrelated receipt; this test fails if that happens.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		Guid receiptC = Guid.NewGuid();
		Guid receiptD = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", new DateOnly(2025, 6, 1), accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", new DateOnly(2025, 6, 1), accountId, 10.00m);
			SeedReceipt(context, receiptC, "Store C", new DateOnly(2025, 7, 1), accountId, 20.00m);
			SeedReceipt(context, receiptD, "Store C", new DateOnly(2025, 7, 1), accountId, 20.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);
		await service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);
		await service.AcceptDuplicateGroupAsync([receiptC, receiptD], CancellationToken.None);

		// Act
		int removed = await service.UnacceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);

		// Assert — only (A,B) went; the disjoint {C,D} acceptance survives.
		removed.Should().Be(1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			List<AcceptedDuplicatePairEntity> remaining = await context.AcceptedDuplicatePairs.ToListAsync();
			remaining.Should().ContainSingle();
			new[] { remaining[0].ReceiptIdA, remaining[0].ReceiptIdB }
				.Should().BeEquivalentTo([receiptC, receiptD]);
		}

		// {C,D} is still suppressed, {A,B} is reported again.
		DuplicateDetectionResult result = await service.GetDuplicatesAsync(
			"dateAndLocation", "exact", 0m, includeAccepted: false, CancellationToken.None);
		result.Groups.Should().ContainSingle();
		result.Groups[0].Receipts.Select(r => r.ReceiptId).Should().BeEquivalentTo([receiptA, receiptB]);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task UnacceptDuplicateGroupAsync_DoesNotReachAnUnrelatedAcceptanceSharingOneReceipt_WhenNotSubmitted()
	{
		// Arrange — {A,B} and {C,D} are separate acceptances. Unaccepting {A,B} must leave {C,D}
		// alone, and the accepted-groups listing must still report it.
		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 6, 1);

		Guid receiptA = Guid.NewGuid();
		Guid receiptB = Guid.NewGuid();
		Guid receiptC = Guid.NewGuid();
		Guid receiptD = Guid.NewGuid();

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			SeedReceipt(context, receiptA, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptB, "Store A", date, accountId, 10.00m);
			SeedReceipt(context, receiptC, "Store D", date, accountId, 30.00m);
			SeedReceipt(context, receiptD, "Store D", date, accountId, 30.00m);
			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);
		await service.AcceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);
		await service.AcceptDuplicateGroupAsync([receiptC, receiptD], CancellationToken.None);

		// Act
		await service.UnacceptDuplicateGroupAsync([receiptA, receiptB], CancellationToken.None);

		// Assert
		AcceptedDuplicatesResult accepted = await service.GetAcceptedDuplicatesAsync(CancellationToken.None);
		accepted.GroupCount.Should().Be(1);
		accepted.Groups[0].Receipts.Select(r => r.ReceiptId).Should().BeEquivalentTo([receiptC, receiptD]);

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAcceptedDuplicatesAsync_ComponentLargerThanTheAcceptCap_IsFullyUndoable()
	{
		// Arrange — accepted groups are connected components, and components merge whenever an
		// acceptance bridges two of them. So a group can grow past the 100-ID accept cap without any
		// single accept call approaching it. Chaining 101 two-receipt accepts is the shortest way to
		// build such a component; the reachable-in-the-UI version is two 51-receipt clusters under
		// matchOn=dateAndLocation joined by one straddling 2-receipt cluster under dateAndTotal.
		//
		// Undo posts every member, so while both endpoints shared the accept validator this group was
		// permanently un-undoable. This test pins that the service side has no such limit.
		const int memberCount = 102;

		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 6, 1);

		List<Guid> receiptIds = [.. Enumerable.Range(0, memberCount).Select(_ => Guid.NewGuid())];

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			foreach (Guid receiptId in receiptIds)
			{
				SeedReceipt(context, receiptId, "Store A", date, accountId, 10.00m);
			}

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		// Chain the accepts so every consecutive pair shares a receipt — one component, 101 pairs.
		for (int i = 0; i < memberCount - 1; i++)
		{
			await service.AcceptDuplicateGroupAsync(
				[receiptIds[i], receiptIds[i + 1]], CancellationToken.None);
		}

		// Act
		AcceptedDuplicatesResult accepted = await service.GetAcceptedDuplicatesAsync(CancellationToken.None);

		// Assert — one component holding every receipt.
		accepted.Groups.Should().ContainSingle();
		accepted.Groups[0].MemberReceiptIds.Should().HaveCount(memberCount);
		accepted.Groups[0].MemberReceiptIds.Should().BeEquivalentTo(receiptIds);

		// Undoing with the full member set clears every pair.
		int removed = await service.UnacceptDuplicateGroupAsync(
			accepted.Groups[0].MemberReceiptIds, CancellationToken.None);
		removed.Should().Be(memberCount - 1);

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			(await context.AcceptedDuplicatePairs.CountAsync()).Should().Be(0);
		}

		contextFactory.ResetDatabase();
	}

	[Fact]
	public async Task GetAcceptedDuplicatesAsync_LargeComponentWithSoftDeletedMembers_StrandsNothing()
	{
		// Arrange — the worst corner. In a component bigger than the accept cap, soft-delete two
		// members so the DISPLAYED list is exactly at the cap while the member set is over it. The
		// report filters soft-deleted receipts out of its snapshot, so "Report again" can never name
		// them; undo was the only path to those pairs, and the shared cap closed it. That left the
		// pairs touching a deleted member reachable by no client-producible set at all.
		const int memberCount = 102;

		IDbContextFactory<ApplicationDbContext> contextFactory = DbContextHelpers.CreateInMemoryContextFactory();
		Guid accountId = Guid.NewGuid();
		DateOnly date = new(2025, 6, 1);

		List<Guid> receiptIds = [.. Enumerable.Range(0, memberCount).Select(_ => Guid.NewGuid())];

		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			foreach (Guid receiptId in receiptIds)
			{
				SeedReceipt(context, receiptId, "Store A", date, accountId, 10.00m);
			}

			await context.SaveChangesAsync();
		}

		ReportService service = new(contextFactory);

		for (int i = 0; i < memberCount - 1; i++)
		{
			await service.AcceptDuplicateGroupAsync(
				[receiptIds[i], receiptIds[i + 1]], CancellationToken.None);
		}

		await SoftDeleteReceiptAsync(contextFactory, receiptIds[0]);
		await SoftDeleteReceiptAsync(contextFactory, receiptIds[^1]);

		// Act
		AcceptedDuplicatesResult accepted = await service.GetAcceptedDuplicatesAsync(CancellationToken.None);

		// Assert — display sits at the accept cap, the member set is over it.
		accepted.Groups.Should().ContainSingle();
		accepted.Groups[0].Receipts.Should().HaveCount(memberCount - 2, "the two deleted receipts do not render");
		accepted.Groups[0].MemberReceiptIds.Should().HaveCount(memberCount, "but undo still needs them");

		int removed = await service.UnacceptDuplicateGroupAsync(
			accepted.Groups[0].MemberReceiptIds, CancellationToken.None);
		removed.Should().Be(memberCount - 1);

		// Nothing stranded — including the pairs that touch the soft-deleted members.
		await using (ApplicationDbContext context = contextFactory.CreateDbContext())
		{
			(await context.AcceptedDuplicatePairs.CountAsync()).Should().Be(0);
			(await context.AcceptedDuplicatePairs.IgnoreQueryFilters()
				.CountAsync(p => p.DeletedAt == null)).Should().Be(0);
		}

		contextFactory.ResetDatabase();
	}

	private static void SeedReceipt(
		ApplicationDbContext context, Guid receiptId, string location, DateOnly date, Guid accountId, decimal amount)
	{
		context.Receipts.Add(
			new ReceiptEntity { Id = receiptId, Location = location, Date = date, TaxAmount = 0m });
		context.Transactions.Add(
			new TransactionEntity { Id = Guid.NewGuid(), ReceiptId = receiptId, AccountId = accountId, Amount = amount, Date = date });
	}

	private static async Task SoftDeleteReceiptAsync(
		IDbContextFactory<ApplicationDbContext> contextFactory, Guid receiptId)
	{
		await using ApplicationDbContext context = contextFactory.CreateDbContext();
		ReceiptEntity receipt = await context.Receipts.SingleAsync(r => r.Id == receiptId);
		context.Receipts.Remove(receipt);
		await context.SaveChangesAsync();
	}
}
