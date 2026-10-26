using Application.Models.Reports;
using Common;
using FluentAssertions;
using Infrastructure.Services;
using Infrastructure.Tests.Repositories;
using SampleData.Entities;

namespace Infrastructure.Tests.Services;

public class CategoryTrendsReportingTests
{
	[Theory]
	[InlineData("daily", "2020-03-01", "2020-03-02")]
	[InlineData("monthly", "2020-03", "2020-04")]
	[InlineData("quarterly", "2020 Q1", "2020 Q2")]
	[InlineData("yearly", "2020", "2021")]
	public async Task Trends_PreservesIdentityCurrencyOtherAndDensePeriods(string granularity, string first, string second)
	{
		var factory = DbContextHelpers.CreateInMemoryContextFactory();
		await using var db = factory.CreateDbContext();
		var receipt = ReceiptEntityGenerator.Generate();
		receipt.Date = new(2020, 3, 1);
		db.Receipts.Add(receipt);
		var items = ReceiptItemEntityGenerator.GenerateList(4, receipt.Id);
		var revision = Guid.NewGuid();
		for (int i = 0; i < items.Count; i++)
		{
			items[i].TotalAmount = i + 1.1234m;
			items[i].TotalAmountCurrency = i < 2 ? Currency.USD : (Currency)1;
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
		await db.SaveChangesAsync();
		DateOnly end = granularity switch
		{
			"daily" => new(2020, 3, 2),
			"monthly" => new(2020, 4, 1),
			"quarterly" => new(2020, 4, 1),
			_ => new(2021, 1, 1)
		};
		var result = await new ReportService(factory).GetCategoryTrendsAsync(receipt.Date, end, granularity, 1, default);
		result.Series.Should().HaveCount(4);
		result.Series.Count(series => series.IsOther).Should().Be(2);
		result.Series.Where(series => !series.IsOther).Select(series => series.Bucket!.CategoryId).Should().OnlyHaveUniqueItems();
		result.Buckets.Select(bucket => bucket.Period).Should().Equal(first, second);
		result.Buckets[1].Amounts.Should().OnlyContain(amount => amount == 0);
		foreach (var currency in result.Series.Select(series => series.Currency).Distinct())
		{
			var indexes = result.Series.Select((series, index) => (series, index)).Where(entry => entry.series.Currency == currency).Select(entry => entry.index);
			result.Buckets[0].Amounts.Where((_, index) => indexes.Contains(index)).Sum().Should().Be(
				result.Snapshot!.Buckets.Where(bucket => bucket.Currency == currency).Sum(bucket => bucket.Amount));
		}
	}
}
