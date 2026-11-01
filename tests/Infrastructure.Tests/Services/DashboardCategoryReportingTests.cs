using Application.Models.Dashboard;
using Application.Models.Reports;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Tests.Repositories;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.Tests.Services;

public class DashboardCategoryReportingTests
{
	[Fact]
	public async Task Report_PreservesReceiptDateTrashAndCurrencyScope_AndDenominatorBeforeLimit()
	{
		var factory = DbContextHelpers.CreateInMemoryContextFactory();
		await using var db = factory.CreateDbContext();
		var receipt = ReceiptEntityGenerator.Generate();
		receipt.Date = new(2020, 3, 1);
		var outside = ReceiptEntityGenerator.Generate();
		outside.Date = new(2021, 3, 1);
		var trashed = ReceiptEntityGenerator.Generate();
		trashed.Date = receipt.Date;
		trashed.DeletedAt = DateTimeOffset.UtcNow;
		db.Receipts.AddRange(receipt, outside, trashed);
		var items = ReceiptItemEntityGenerator.GenerateList(4, receipt.Id);
		items[0].TotalAmount = 10.1234m;
		items[1].TotalAmount = 30.3702m;
		items[2].TotalAmount = 20m;
		items[3].TotalAmount = 900m;
		items[3].DeletedAt = DateTimeOffset.UtcNow;
		var revision = Guid.NewGuid();
		for (int i = 0; i < 4; i++)
		{
			// Match the existing PostgreSQL fixture: preserve unknown stored currency values as separate buckets.
			items[i].TotalAmountCurrency = i == 2 ? (Currency)1 : Currency.USD;
			var category = Guid.NewGuid();
			var assignment = Guid.NewGuid();
			db.TaxonomyNodes.Add(new() { RevisionId = revision, CategoryId = category, Name = "Same name" });
			db.Classifications.Add(new()
			{
				Id = assignment,
				ReceiptItemId = items[i].Id,
				CategoryId = category,
				TaxonomyRevisionId = revision,
				RecordedAt = DateTimeOffset.UtcNow
			});
			db.CurrentClassifications.Add(new() { ReceiptItemId = items[i].Id, AssignmentId = assignment });
		}
		db.ReceiptItems.AddRange(items);
		db.ReceiptItems.Add(ReceiptItemEntityGenerator.Generate(outside.Id));
		db.ReceiptItems.Add(ReceiptItemEntityGenerator.Generate(trashed.Id));
		await db.SaveChangesAsync();
		DashboardService service = new(factory);
		var actual = await service.GetSpendingByCategoryAsync(receipt.Date, receipt.Date, 2, CancellationToken.None);
		actual.Items.Should().HaveCount(2);
		actual.Items[0].Amount.Should().Be(30.3702m);
		actual.Items[0].Percentage.Should().Be(75m);
		actual.Items[1].Bucket!.Currency.Should().Be("1");
		actual.Items[1].Percentage.Should().Be(100m);
		actual.Snapshot!.Buckets.Should().HaveCount(3);
		actual.Snapshot.Buckets.Sum(b => b.PurchaseCount).Should().Be(3);
		(await db.Classifications.CountAsync()).Should().Be(4);
	}

	[Fact]
	public async Task Report_RejectsUnknownCapturedRevision()
	{
		var factory = DbContextHelpers.CreateInMemoryContextFactory();
		DashboardService service = new(factory);
		Func<Task> action = () => service.GetSpendingByCategoryAsync(new(2020, 1, 1), new(2020, 12, 31), 10,
			CancellationToken.None, new(CategoryReportView.CurrentHierarchy, CapturedCurrentRevisionId: Guid.NewGuid()));
		await action.Should().ThrowAsync<ArgumentException>().WithParameterName("capturedCurrentRevisionId");
	}

	[Fact]
	public async Task Summary_MostUsedCategory_IsTopLevelItemTypeWithMostPurchases_IgnoringLegacyLabels()
	{
		var factory = DbContextHelpers.CreateInMemoryContextFactory();
		await using var db = factory.CreateDbContext();
		var receipt = ReceiptEntityGenerator.Generate();
		receipt.Date = new(2020, 3, 1);
		db.Receipts.Add(receipt);
		Guid revision = Guid.NewGuid(), food = Guid.NewGuid(), dairy = Guid.NewGuid(), household = Guid.NewGuid();
		db.TaxonomyHeads.Add(new() { Id = 1, RevisionId = revision });
		db.TaxonomyNodes.AddRange(
			new() { RevisionId = revision, CategoryId = food, Name = "Food" },
			new() { RevisionId = revision, CategoryId = dairy, ParentId = food, Name = "Dairy" },
			new() { RevisionId = revision, CategoryId = household, Name = "Household" });
		// Food: two Dairy purchases roll up with one direct Food purchase; Household: two purchases.
		foreach (Guid category in new[] { dairy, dairy, food, household, household })
		{
			Assign(db, AddItem(db, receipt.Id, "Household"), revision, category);
		}
		// The most frequent legacy label belongs to items with no item type, which never count.
		for (int i = 0; i < 4; i++) { AddItem(db, receipt.Id, "Legacy favourite"); }
		Assign(db, AddItem(db, receipt.Id, "Legacy favourite"), revision, null);
		await db.SaveChangesAsync();

		var summary = await new DashboardService(factory).GetSummaryAsync(receipt.Date, receipt.Date, CancellationToken.None);

		summary.MostUsedCategory.Should().Be(new NameCountResult("Food", 3));
	}

	[Fact]
	public async Task Summary_MostUsedCategory_BreaksTiesByNameAndIsEmptyWithoutItemTypes()
	{
		var factory = DbContextHelpers.CreateInMemoryContextFactory();
		await using var db = factory.CreateDbContext();
		var tied = ReceiptEntityGenerator.Generate();
		tied.Date = new(2020, 3, 1);
		var untyped = ReceiptEntityGenerator.Generate();
		untyped.Date = new(2021, 3, 1);
		db.Receipts.AddRange(tied, untyped);
		Guid revision = Guid.NewGuid(), beta = Guid.NewGuid(), alpha = Guid.NewGuid();
		db.TaxonomyHeads.Add(new() { Id = 1, RevisionId = revision });
		db.TaxonomyNodes.AddRange(
			new() { RevisionId = revision, CategoryId = beta, Name = "Beta" },
			new() { RevisionId = revision, CategoryId = alpha, Name = "Alpha" });
		Assign(db, AddItem(db, tied.Id, "Beta"), revision, beta);
		Assign(db, AddItem(db, tied.Id, "Beta"), revision, alpha);
		AddItem(db, untyped.Id, "Groceries");
		await db.SaveChangesAsync();
		DashboardService service = new(factory);

		var tie = await service.GetSummaryAsync(tied.Date, tied.Date, CancellationToken.None);
		var none = await service.GetSummaryAsync(untyped.Date, untyped.Date, CancellationToken.None);

		tie.MostUsedCategory.Should().Be(new NameCountResult("Alpha", 1));
		none.TotalReceipts.Should().Be(1);
		none.MostUsedCategory.Should().Be(new NameCountResult(null, 0));
	}

	private static ReceiptItemEntity AddItem(ApplicationDbContext db, Guid receiptId, string legacyCategory)
	{
		var item = ReceiptItemEntityGenerator.Generate(receiptId);
		item.Category = legacyCategory;
		db.ReceiptItems.Add(item);
		return item;
	}

	private static void Assign(ApplicationDbContext db, ReceiptItemEntity item, Guid revision, Guid? category)
	{
		var assignment = Guid.NewGuid();
		db.Classifications.Add(new()
		{
			Id = assignment,
			ReceiptItemId = item.Id,
			CategoryId = category,
			TaxonomyRevisionId = revision,
			RecordedAt = DateTimeOffset.UtcNow
		});
		db.CurrentClassifications.Add(new() { ReceiptItemId = item.Id, AssignmentId = assignment });
	}
}
