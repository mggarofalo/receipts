using Application.Models.Reports;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Envelopes;
using Infrastructure.Services;
using Infrastructure.Tests.Repositories;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.Tests.Services;

public class BudgetTrendsReportingTests
{
	private static readonly DateOnly Start = new(2025, 1, 1);
	private static readonly DateOnly End = new(2025, 1, 31);
	private readonly IDbContextFactory<ApplicationDbContext> _factory = DbContextHelpers.CreateInMemoryContextFactory();

	[Fact]
	public async Task BudgetTrends_ByEnvelope_GroupsRecordedEnvelopes_KeepsArchivedNames_AndLabelsNone()
	{
		// Arrange
		EnvelopeEntity groceries = Envelope("Groceries");
		EnvelopeEntity inactive = Envelope("Retired", isActive: false);
		EnvelopeEntity deleted = Envelope("Deleted envelope", isActive: false, deletedAt: DateTimeOffset.UtcNow);
		await using (ApplicationDbContext db = _factory.CreateDbContext())
		{
			db.Envelopes.AddRange(groceries, inactive, deleted);
			ReceiptEntity receipt = AddReceipt(db, new(2025, 1, 10));
			AddItem(db, receipt, 10m, envelopeId: groceries.Id);
			AddItem(db, receipt, 5m, envelopeId: groceries.Id);
			AddItem(db, receipt, 3m, envelopeId: inactive.Id);
			AddItem(db, receipt, 2m, envelopeId: deleted.Id);
			AddItem(db, receipt, 4m);
			await db.SaveChangesAsync();
		}

		// Act
		BudgetTrendsResult result = await Service().GetBudgetTrendsAsync(Start, End, "monthly", 50,
			BudgetTrendsDimension.Envelope, null, null, default);

		// Assert
		result.Dimension.Should().Be(BudgetTrendsDimension.Envelope);
		result.Series.Should().Equal(
			new BudgetTrendsSeriesResult(groceries.Id, "Groceries", true, "USD", false, 15m, 2),
			new BudgetTrendsSeriesResult(null, "No envelope", true, "USD", false, 4m, 1),
			new BudgetTrendsSeriesResult(inactive.Id, "Retired", false, "USD", false, 3m, 1),
			new BudgetTrendsSeriesResult(deleted.Id, "Deleted envelope", false, "USD", false, 2m, 1));
		result.Buckets.Should().ContainSingle();
		result.Buckets[0].Period.Should().Be("2025-01");
		result.Buckets[0].Amounts.Should().Equal(15m, 4m, 3m, 2m);
	}

	[Fact]
	public async Task BudgetTrends_ByOccasion_GroupsRecordedOccasions_IgnoringEnvelopes()
	{
		// Arrange
		EnvelopeEntity envelope = Envelope("Gifts");
		OccasionEntity birthday = Occasion("Birthday");
		OccasionEntity archived = Occasion("Old trip", isActive: false, deletedAt: DateTimeOffset.UtcNow);
		await using (ApplicationDbContext db = _factory.CreateDbContext())
		{
			db.Envelopes.Add(envelope);
			db.Occasions.AddRange(birthday, archived);
			ReceiptEntity receipt = AddReceipt(db, new(2025, 1, 5));
			AddItem(db, receipt, 20m, envelopeId: envelope.Id, occasionId: birthday.Id);
			AddItem(db, receipt, 7m, occasionId: archived.Id);
			AddItem(db, receipt, 1m, envelopeId: envelope.Id);
			AddItem(db, receipt, 2m);
			await db.SaveChangesAsync();
		}

		// Act
		BudgetTrendsResult result = await Service().GetBudgetTrendsAsync(Start, End, "monthly", 50,
			BudgetTrendsDimension.Occasion, null, null, default);

		// Assert
		result.Dimension.Should().Be(BudgetTrendsDimension.Occasion);
		result.Series.Should().Equal(
			new BudgetTrendsSeriesResult(birthday.Id, "Birthday", true, "USD", false, 20m, 1),
			new BudgetTrendsSeriesResult(archived.Id, "Old trip", false, "USD", false, 7m, 1),
			new BudgetTrendsSeriesResult(null, "No occasion", true, "USD", false, 3m, 2));
		result.Buckets.Single().Amounts.Should().Equal(20m, 7m, 3m);
	}

	[Fact]
	public async Task BudgetTrends_ExcludesDeletedItems_DeletedReceipts_AndPurchasesOutsideTheRange()
	{
		// Arrange
		EnvelopeEntity envelope = Envelope("Household");
		await using (ApplicationDbContext db = _factory.CreateDbContext())
		{
			db.Envelopes.Add(envelope);
			ReceiptEntity live = AddReceipt(db, new(2025, 1, 15));
			AddItem(db, live, 6m, envelopeId: envelope.Id);
			AddItem(db, live, 100m, envelopeId: envelope.Id, deletedAt: DateTimeOffset.UtcNow);
			ReceiptEntity deletedReceipt = AddReceipt(db, new(2025, 1, 15));
			AddItem(db, deletedReceipt, 200m, envelopeId: envelope.Id);
			AddItem(db, AddReceipt(db, new(2024, 12, 31)), 300m, envelopeId: envelope.Id);
			AddItem(db, AddReceipt(db, new(2025, 2, 1)), 400m, envelopeId: envelope.Id);
			await db.SaveChangesAsync();
			// A receipt admits no children once deleted, so delete it after its item is saved.
			deletedReceipt.DeletedAt = DateTimeOffset.UtcNow;
			await db.SaveChangesAsync();
		}

		// Act
		BudgetTrendsResult result = await Service().GetBudgetTrendsAsync(Start, End, "monthly", 7,
			BudgetTrendsDimension.Envelope, null, null, default);

		// Assert
		result.Series.Should().ContainSingle().Which.Should().Be(
			new BudgetTrendsSeriesResult(envelope.Id, "Household", true, "USD", false, 6m, 1));
		result.Buckets.Single().Amounts.Should().Equal(6m);
	}

	[Fact]
	public async Task BudgetTrends_TopNAndOther_ArePerCurrency_AndNeverMixCurrencies()
	{
		// Arrange
		Currency other = (Currency)1;
		EnvelopeEntity a = Envelope("A");
		EnvelopeEntity b = Envelope("B");
		EnvelopeEntity c = Envelope("C");
		await using (ApplicationDbContext db = _factory.CreateDbContext())
		{
			db.Envelopes.AddRange(a, b, c);
			ReceiptEntity receipt = AddReceipt(db, new(2025, 1, 20));
			AddItem(db, receipt, 30m, envelopeId: a.Id);
			AddItem(db, receipt, 20m, envelopeId: b.Id);
			AddItem(db, receipt, 10m, envelopeId: c.Id);
			AddItem(db, receipt, 1m, envelopeId: a.Id, currency: other);
			AddItem(db, receipt, 5m, envelopeId: b.Id, currency: other);
			await db.SaveChangesAsync();
		}

		// Act
		BudgetTrendsResult result = await Service().GetBudgetTrendsAsync(Start, End, "monthly", 1,
			BudgetTrendsDimension.Envelope, null, null, default);

		// Assert: currencies are ordered ordinally and each ranks and collapses its own purchases.
		result.Series.Should().Equal(
			new BudgetTrendsSeriesResult(b.Id, "B", true, "1", false, 5m, 1),
			new BudgetTrendsSeriesResult(null, "Other", true, "1", true, 1m, 1),
			new BudgetTrendsSeriesResult(a.Id, "A", true, "USD", false, 30m, 1),
			new BudgetTrendsSeriesResult(null, "Other", true, "USD", true, 30m, 2));
		result.Buckets.Single().Amounts.Should().Equal(5m, 1m, 30m, 30m);
	}

	[Fact]
	public async Task BudgetTrends_OtherSeries_DoesNotAbsorbTheNamedNoEnvelopeSeries()
	{
		// Arrange
		EnvelopeEntity small = Envelope("Small");
		await using (ApplicationDbContext db = _factory.CreateDbContext())
		{
			db.Envelopes.Add(small);
			ReceiptEntity receipt = AddReceipt(db, new(2025, 1, 2));
			AddItem(db, receipt, 50m);
			AddItem(db, receipt, 2m, envelopeId: small.Id);
			await db.SaveChangesAsync();
		}

		// Act
		BudgetTrendsResult result = await Service().GetBudgetTrendsAsync(Start, End, "monthly", 1,
			BudgetTrendsDimension.Envelope, null, null, default);

		// Assert
		result.Series.Select(series => (series.Id, series.Name, series.IsOther, series.Total)).Should().Equal(
			((Guid?)null, "No envelope", false, 50m), ((Guid?)null, "Other", true, 2m));
		result.Buckets.Single().Amounts.Should().Equal(50m, 2m);
	}

	[Fact]
	public async Task BudgetTrends_EnvelopeFilter_NarrowsPurchasesBeforeGroupingByOccasion()
	{
		// Arrange
		EnvelopeEntity gifts = Envelope("Gifts");
		EnvelopeEntity food = Envelope("Food");
		OccasionEntity party = Occasion("Party");
		await using (ApplicationDbContext db = _factory.CreateDbContext())
		{
			db.Envelopes.AddRange(gifts, food);
			db.Occasions.Add(party);
			ReceiptEntity receipt = AddReceipt(db, new(2025, 1, 3));
			AddItem(db, receipt, 8m, envelopeId: gifts.Id, occasionId: party.Id);
			AddItem(db, receipt, 4m, envelopeId: gifts.Id);
			AddItem(db, receipt, 90m, envelopeId: food.Id, occasionId: party.Id);
			await db.SaveChangesAsync();
		}

		// Act
		BudgetTrendsResult result = await Service().GetBudgetTrendsAsync(Start, End, "monthly", 7,
			BudgetTrendsDimension.Occasion, gifts.Id, null, default);

		// Assert
		result.Series.Select(series => (series.Name, series.Total)).Should().Equal(("Party", 8m), ("No occasion", 4m));
	}

	[Fact]
	public async Task BudgetTrends_OccasionFilter_NarrowsPurchasesBeforeGroupingByEnvelope()
	{
		// Arrange
		EnvelopeEntity gifts = Envelope("Gifts");
		EnvelopeEntity food = Envelope("Food");
		OccasionEntity party = Occasion("Party");
		OccasionEntity trip = Occasion("Trip");
		await using (ApplicationDbContext db = _factory.CreateDbContext())
		{
			db.Envelopes.AddRange(gifts, food);
			db.Occasions.AddRange(party, trip);
			ReceiptEntity receipt = AddReceipt(db, new(2025, 1, 3));
			AddItem(db, receipt, 8m, envelopeId: gifts.Id, occasionId: party.Id);
			AddItem(db, receipt, 12m, envelopeId: food.Id, occasionId: party.Id);
			AddItem(db, receipt, 90m, envelopeId: food.Id, occasionId: trip.Id);
			AddItem(db, receipt, 70m, envelopeId: gifts.Id);
			await db.SaveChangesAsync();
		}

		// Act
		BudgetTrendsResult result = await Service().GetBudgetTrendsAsync(Start, End, "monthly", 7,
			BudgetTrendsDimension.Envelope, null, party.Id, default);

		// Assert
		result.Series.Select(series => (series.Name, series.Total)).Should().Equal(("Food", 12m), ("Gifts", 8m));
	}

	[Fact]
	public async Task BudgetTrends_BothFilters_RequireBothToMatch()
	{
		// Arrange
		EnvelopeEntity gifts = Envelope("Gifts");
		OccasionEntity party = Occasion("Party");
		await using (ApplicationDbContext db = _factory.CreateDbContext())
		{
			db.Envelopes.Add(gifts);
			db.Occasions.Add(party);
			ReceiptEntity receipt = AddReceipt(db, new(2025, 1, 3));
			AddItem(db, receipt, 8m, envelopeId: gifts.Id, occasionId: party.Id);
			AddItem(db, receipt, 4m, envelopeId: gifts.Id);
			AddItem(db, receipt, 2m, occasionId: party.Id);
			await db.SaveChangesAsync();
		}

		// Act
		BudgetTrendsResult result = await Service().GetBudgetTrendsAsync(Start, End, "monthly", 7,
			BudgetTrendsDimension.Envelope, gifts.Id, party.Id, default);

		// Assert
		result.Series.Should().ContainSingle().Which.Total.Should().Be(8m);
	}

	[Theory]
	[InlineData("daily", "2025-01-15", "2025-03-01", 46)]
	[InlineData("Monthly", "2025-01", "2025-03", 3)]
	[InlineData("quarterly", "2025 Q1", "2025 Q1", 1)]
	[InlineData("yearly", "2025", "2025", 1)]
	public async Task BudgetTrends_Buckets_AreDenseAndZeroFilled(string granularity, string first, string last, int count)
	{
		// Arrange
		EnvelopeEntity envelope = Envelope("Fuel");
		await using (ApplicationDbContext db = _factory.CreateDbContext())
		{
			db.Envelopes.Add(envelope);
			AddItem(db, AddReceipt(db, new(2025, 1, 15)), 10m, envelopeId: envelope.Id);
			AddItem(db, AddReceipt(db, new(2025, 3, 1)), 30m, envelopeId: envelope.Id);
			await db.SaveChangesAsync();
		}

		// Act
		BudgetTrendsResult result = await Service().GetBudgetTrendsAsync(new(2025, 1, 15), new(2025, 3, 1), granularity, 7,
			BudgetTrendsDimension.Envelope, null, null, default);

		// Assert
		result.Buckets.Should().HaveCount(count);
		result.Buckets[0].Period.Should().Be(first);
		result.Buckets[^1].Period.Should().Be(last);
		result.Buckets.Sum(bucket => bucket.Amounts.Single()).Should().Be(40m);
		if (count > 1)
		{
			result.Buckets[0].Amounts.Should().Equal(10m);
			result.Buckets[^1].Amounts.Should().Equal(30m);
			result.Buckets.Skip(1).SkipLast(1).Should().OnlyContain(bucket => bucket.Amounts.Single() == 0m);
		}
	}

	[Fact]
	public async Task BudgetTrends_OpenStartDate_BeginsAtTheFirstPurchase()
	{
		// Arrange
		await using (ApplicationDbContext db = _factory.CreateDbContext())
		{
			AddItem(db, AddReceipt(db, new(2025, 2, 10)), 10m);
			await db.SaveChangesAsync();
		}

		// Act
		BudgetTrendsResult result = await Service().GetBudgetTrendsAsync(DateOnly.MinValue, new(2025, 4, 30), "monthly", 7,
			BudgetTrendsDimension.Envelope, null, null, default);

		// Assert
		result.Buckets.Select(bucket => bucket.Period).Should().Equal("2025-02", "2025-03", "2025-04");
	}

	[Theory]
	[InlineData("monthly", new[] { "2025-01", "2025-02", "2025-03", "2025-04" }, 2)]
	[InlineData("quarterly", new[] { "2024 Q4", "2025 Q1", "2025 Q2" }, 1)]
	public async Task BudgetTrends_ExplicitStartDate_ZeroFillsBucketsBeforeTheFirstPurchase(
		string granularity, string[] periods, int purchaseBucket)
	{
		// Arrange
		await using (ApplicationDbContext db = _factory.CreateDbContext())
		{
			AddItem(db, AddReceipt(db, new(2025, 3, 10)), 10m);
			await db.SaveChangesAsync();
		}
		DateOnly start = granularity == "quarterly" ? new(2024, 12, 15) : new(2025, 1, 1);

		// Act
		BudgetTrendsResult result = await Service().GetBudgetTrendsAsync(start, new(2025, 4, 30), granularity, 7,
			BudgetTrendsDimension.Envelope, null, null, default);

		// Assert
		result.Buckets.Select(bucket => bucket.Period).Should().Equal(periods);
		result.Buckets.Select(bucket => bucket.Amounts.Single()).Should().Equal(
			periods.Select((_, index) => index == purchaseBucket ? 10m : 0m));
	}

	[Fact]
	public async Task BudgetTrends_NoPurchases_ReturnsEmptySeriesAndBuckets()
	{
		// Act
		BudgetTrendsResult result = await Service().GetBudgetTrendsAsync(Start, End, "monthly", 7,
			BudgetTrendsDimension.Occasion, null, null, default);

		// Assert
		result.Should().BeEquivalentTo(new BudgetTrendsResult(BudgetTrendsDimension.Occasion, [], []));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(51)]
	public async Task BudgetTrends_RejectsTopNOutOfRange(int topN)
	{
		// Act
		Func<Task> act = () => Service().GetBudgetTrendsAsync(Start, End, "monthly", topN,
			BudgetTrendsDimension.Envelope, null, null, default);

		// Assert
		(await act.Should().ThrowAsync<ArgumentOutOfRangeException>()).Which.ParamName.Should().Be("topN");
	}

	[Fact]
	public async Task BudgetTrends_RejectsUnknownGranularity()
	{
		// Act
		Func<Task> act = () => Service().GetBudgetTrendsAsync(Start, End, "weekly", 7,
			BudgetTrendsDimension.Envelope, null, null, default);

		// Assert
		(await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("granularity");
	}

	[Fact]
	public async Task BudgetTrends_RejectsStartAfterEnd()
	{
		// Act
		Func<Task> act = () => Service().GetBudgetTrendsAsync(End, Start, "monthly", 7,
			BudgetTrendsDimension.Envelope, null, null, default);

		// Assert
		(await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("startDate");
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task CategoryTrends_HonoursEnvelopeAndOccasionFilters(bool byEnvelope)
	{
		// Arrange
		EnvelopeEntity envelope = Envelope("Gifts");
		OccasionEntity occasion = Occasion("Party");
		Guid revision = Guid.NewGuid();
		Guid matching = Guid.NewGuid();
		Guid excluded = Guid.NewGuid();
		await using (ApplicationDbContext db = _factory.CreateDbContext())
		{
			db.Envelopes.Add(envelope);
			db.Occasions.Add(occasion);
			ReceiptEntity receipt = AddReceipt(db, new(2025, 1, 9));
			ReceiptItemEntity kept = AddItem(db, receipt, 9m,
				envelopeId: byEnvelope ? envelope.Id : null, occasionId: byEnvelope ? null : occasion.Id);
			ReceiptItemEntity dropped = AddItem(db, receipt, 50m);
			Classify(db, kept, matching, revision, "Matching");
			Classify(db, dropped, excluded, revision, "Excluded");
			await db.SaveChangesAsync();
		}
		CategoryReportOptions options = byEnvelope ? new(EnvelopeId: envelope.Id) : new(OccasionId: occasion.Id);

		// Act
		CategoryTrendsResult filtered = await Service().GetCategoryTrendsAsync(Start, End, "monthly", 7, default, options);
		CategoryTrendsResult unfiltered = await Service().GetCategoryTrendsAsync(Start, End, "monthly", 7, default, new());

		// Assert
		filtered.Series.Should().ContainSingle().Which.Bucket!.CategoryId.Should().Be(matching);
		filtered.Buckets.Single().Amounts.Should().Equal(9m);
		unfiltered.Series.Select(series => series.Bucket!.CategoryId).Should().BeEquivalentTo([matching, excluded]);
	}

	private ReportService Service() => new(_factory);

	private static EnvelopeEntity Envelope(string name, bool isActive = true, DateTimeOffset? deletedAt = null)
		=> new() { Id = Guid.NewGuid(), Name = name, IsActive = isActive, DeletedAt = deletedAt };

	private static OccasionEntity Occasion(string name, bool isActive = true, DateTimeOffset? deletedAt = null)
		=> new() { Id = Guid.NewGuid(), Name = name, IsActive = isActive, DeletedAt = deletedAt };

	private static ReceiptEntity AddReceipt(ApplicationDbContext db, DateOnly date)
	{
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.Date = date;
		db.Receipts.Add(receipt);
		return receipt;
	}

	private static ReceiptItemEntity AddItem(ApplicationDbContext db, ReceiptEntity receipt, decimal amount,
		Guid? envelopeId = null, Guid? occasionId = null, Currency currency = Currency.USD, DateTimeOffset? deletedAt = null)
	{
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.TotalAmount = amount;
		item.TotalAmountCurrency = currency;
		item.UnitPrice = amount;
		item.UnitPriceCurrency = currency;
		item.EnvelopeId = envelopeId;
		item.OccasionId = occasionId;
		item.DeletedAt = deletedAt;
		db.ReceiptItems.Add(item);
		return item;
	}

	private static void Classify(ApplicationDbContext db, ReceiptItemEntity item, Guid category, Guid revision, string name)
	{
		Guid assignment = Guid.NewGuid();
		db.TaxonomyNodes.Add(new() { RevisionId = revision, CategoryId = category, Name = name });
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
