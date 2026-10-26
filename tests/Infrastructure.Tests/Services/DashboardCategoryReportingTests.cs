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
}

