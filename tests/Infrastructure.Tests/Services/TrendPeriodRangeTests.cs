using Application.Exceptions;
using Application.Models.Reports;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Tests.Repositories;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.Tests.Services;

/// <summary>Bucket ranges of the category and budget trend reports: the period cap, the calendar's end, and open starts.</summary>
public class TrendPeriodRangeTests
{
	private readonly IDbContextFactory<ApplicationDbContext> _factory = DbContextHelpers.CreateInMemoryContextFactory();
	private readonly Guid _revision = Guid.NewGuid();

	[Fact]
	public async Task CategoryTrends_DailyOverTwentyYears_ThrowsRangeTooLarge()
	{
		// Arrange
		await SeedPurchaseAsync(new(2010, 6, 1));

		// Act
		Func<Task> act = () => Service().GetCategoryTrendsAsync(new(2000, 1, 1), new(2020, 1, 1), "daily", 5, default);

		// Assert
		(await act.Should().ThrowAsync<ReportRangeTooLargeException>())
			.Which.Message.Should().Contain(ReportService.MaxTrendPeriods.ToString()).And.Contain("daily");
	}

	[Fact]
	public async Task CategoryTrends_ExactlyTheMaximumNumberOfPeriods_IsReturned()
	{
		// Arrange
		DateOnly start = new(2000, 1, 1);
		DateOnly last = start.AddDays(ReportService.MaxTrendPeriods - 1);
		await SeedPurchaseAsync(start);

		// Act
		CategoryTrendsResult atLimit = await Service().GetCategoryTrendsAsync(start, last, "daily", 5, default);
		Func<Task> overLimit = () => Service().GetCategoryTrendsAsync(start, last.AddDays(1), "daily", 5, default);

		// Assert
		atLimit.Buckets.Should().HaveCount(ReportService.MaxTrendPeriods);
		atLimit.Buckets[^1].Period.Should().Be(last.ToString("yyyy-MM-dd"));
		await overLimit.Should().ThrowAsync<ReportRangeTooLargeException>();
	}

	[Theory]
	[InlineData("monthly")]
	[InlineData("quarterly")]
	[InlineData("daily")]
	public async Task CategoryTrends_EndingOnTheLastRepresentableDay_ThrowsRangeTooLarge_NotArgumentOutOfRange(string granularity)
	{
		// Arrange
		await SeedPurchaseAsync(new(2020, 3, 1));

		// Act
		Func<Task> act = () => Service().GetCategoryTrendsAsync(new(2020, 1, 1), DateOnly.MaxValue, granularity, 5, default);

		// Assert
		await act.Should().ThrowAsync<ReportRangeTooLargeException>();
	}

	[Fact]
	public async Task CategoryTrends_MonthlyThroughTheLastRepresentableMonth_StopsWithoutOverflow()
	{
		// Arrange
		await SeedPurchaseAsync(new(9999, 2, 1));

		// Act
		CategoryTrendsResult result = await Service().GetCategoryTrendsAsync(new(9999, 1, 1), DateOnly.MaxValue, "monthly", 5, default);

		// Assert
		result.Buckets.Select(bucket => bucket.Period).Should().Equal(
			Enumerable.Range(1, 12).Select(month => $"9999-{month:D2}"));
		result.Buckets[1].Amounts.Should().Equal(5m);
	}

	[Fact]
	public async Task CategoryTrends_YearlyThroughTheLastRepresentableYear_StopsWithoutOverflow()
	{
		// Arrange — 5000 through 9999 is exactly the cap, and the year after 9999 cannot be represented.
		await SeedPurchaseAsync(new(5000, 1, 1));

		// Act
		CategoryTrendsResult result = await Service().GetCategoryTrendsAsync(new(5000, 1, 1), DateOnly.MaxValue, "yearly", 5, default);

		// Assert
		result.Buckets.Should().HaveCount(ReportService.MaxTrendPeriods);
		result.Buckets[0].Period.Should().Be("5000");
		result.Buckets[^1].Period.Should().Be("9999");
	}

	[Fact]
	public async Task CategoryTrends_OpenStart_BeginsAtTheFirstPurchasePeriod()
	{
		// Arrange
		await SeedPurchaseAsync(new(2020, 3, 15));
		await SeedPurchaseAsync(new(2020, 5, 2));

		// Act
		CategoryTrendsResult result = await Service().GetCategoryTrendsAsync(DateOnly.MinValue, new(2020, 6, 30), "monthly", 5, default);

		// Assert
		result.Buckets.Select(bucket => bucket.Period).Should().Equal("2020-03", "2020-04", "2020-05", "2020-06");
		result.Buckets.Select(bucket => bucket.Amounts.Sum()).Should().Equal(5m, 0m, 5m, 0m);
	}

	[Fact]
	public async Task CategoryTrends_ExplicitStart_IsZeroFilledFromTheStart()
	{
		// Arrange
		await SeedPurchaseAsync(new(2020, 3, 15));

		// Act
		CategoryTrendsResult result = await Service().GetCategoryTrendsAsync(new(2020, 1, 1), new(2020, 4, 30), "monthly", 5, default);

		// Assert
		result.Buckets.Select(bucket => bucket.Period).Should().Equal("2020-01", "2020-02", "2020-03", "2020-04");
		result.Buckets.Select(bucket => bucket.Amounts.Sum()).Should().Equal(0m, 0m, 5m, 0m);
	}

	[Fact]
	public async Task CategoryTrends_OpenStart_IgnoresPurchasesOutsideTheCategoryFilter()
	{
		// Arrange — an older purchase in another category must not pull the first bucket back.
		Guid wanted = Guid.NewGuid();
		await SeedPurchaseAsync(new(2015, 1, 10));
		await SeedPurchaseAsync(new(2020, 3, 15), wanted);

		// Act
		CategoryTrendsResult result = await Service().GetCategoryTrendsAsync(DateOnly.MinValue, new(2020, 4, 30), "monthly", 5,
			default, new CategoryReportOptions(CategoryId: wanted));

		// Assert
		result.Series.Should().ContainSingle().Which.Bucket!.CategoryId.Should().Be(wanted);
		result.Buckets.Select(bucket => bucket.Period).Should().Equal("2020-03", "2020-04");
	}

	[Fact]
	public async Task CategoryTrends_OpenStart_IgnoresPurchasesOutsideTheEnvelopeFilter()
	{
		// Arrange
		Guid envelope = Guid.NewGuid();
		await SeedPurchaseAsync(new(2015, 1, 10));
		await SeedPurchaseAsync(new(2020, 3, 15), envelopeId: envelope);

		// Act
		CategoryTrendsResult result = await Service().GetCategoryTrendsAsync(DateOnly.MinValue, new(2020, 4, 30), "monthly", 5,
			default, new CategoryReportOptions(EnvelopeId: envelope));

		// Assert
		result.Buckets.Select(bucket => bucket.Period).Should().Equal("2020-03", "2020-04");
	}

	[Fact]
	public async Task BudgetTrends_DailyOverTwentyYears_ThrowsRangeTooLarge()
	{
		// Arrange
		await SeedPurchaseAsync(new(2010, 6, 1));

		// Act
		Func<Task> act = () => Service().GetBudgetTrendsAsync(new(2000, 1, 1), new(2020, 1, 1), "daily", 5,
			BudgetTrendsDimension.Envelope, null, null, default);

		// Assert
		(await act.Should().ThrowAsync<ReportRangeTooLargeException>())
			.Which.Message.Should().Contain(ReportService.MaxTrendPeriods.ToString());
	}

	[Fact]
	public async Task BudgetTrends_MonthlyToTheLastRepresentableDay_ThrowsRangeTooLarge_NotArgumentOutOfRange()
	{
		// Arrange
		await SeedPurchaseAsync(new(2020, 3, 1));

		// Act
		Func<Task> act = () => Service().GetBudgetTrendsAsync(new(2020, 1, 1), DateOnly.MaxValue, "monthly", 5,
			BudgetTrendsDimension.Envelope, null, null, default);

		// Assert
		await act.Should().ThrowAsync<ReportRangeTooLargeException>();
	}

	[Fact]
	public async Task BudgetTrends_YearlyThroughTheLastRepresentableYear_StopsWithoutOverflow()
	{
		// Arrange
		await SeedPurchaseAsync(new(9990, 6, 1));

		// Act
		BudgetTrendsResult result = await Service().GetBudgetTrendsAsync(new(9990, 1, 1), DateOnly.MaxValue, "yearly", 5,
			BudgetTrendsDimension.Envelope, null, null, default);

		// Assert
		result.Buckets.Select(bucket => bucket.Period).Should().Equal(Enumerable.Range(9990, 10).Select(year => year.ToString()));
		result.Buckets[0].Amounts.Should().Equal(5m);
	}

	[Fact]
	public async Task BudgetTrends_OpenStart_BeginsAtTheFirstPurchasePeriod()
	{
		// Arrange
		await SeedPurchaseAsync(new(2020, 3, 15));

		// Act
		BudgetTrendsResult result = await Service().GetBudgetTrendsAsync(DateOnly.MinValue, new(2020, 5, 31), "monthly", 5,
			BudgetTrendsDimension.Envelope, null, null, default);

		// Assert
		result.Buckets.Select(bucket => bucket.Period).Should().Equal("2020-03", "2020-04", "2020-05");
	}

	private ReportService Service() => new(_factory);

	/// <summary>One classified 5 USD purchase on the date.</summary>
	private async Task SeedPurchaseAsync(DateOnly date, Guid? category = null, Guid? envelopeId = null)
	{
		await using ApplicationDbContext db = _factory.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.Date = date;
		db.Receipts.Add(receipt);
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.TotalAmount = 5m;
		item.TotalAmountCurrency = Currency.USD;
		item.EnvelopeId = envelopeId;
		db.ReceiptItems.Add(item);
		Guid categoryId = category ?? Guid.NewGuid();
		Guid assignment = Guid.NewGuid();
		db.TaxonomyNodes.Add(new() { RevisionId = _revision, CategoryId = categoryId, Name = "Category " + categoryId });
		db.Classifications.Add(new()
		{
			Id = assignment,
			ReceiptItemId = item.Id,
			CategoryId = categoryId,
			TaxonomyRevisionId = _revision,
			RecordedAt = DateTimeOffset.UtcNow
		});
		db.CurrentClassifications.Add(new() { ReceiptItemId = item.Id, AssignmentId = assignment });
		await db.SaveChangesAsync();
	}
}
