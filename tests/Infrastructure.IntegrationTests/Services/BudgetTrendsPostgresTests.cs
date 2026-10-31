using Application.Models.Reports;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Envelopes;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class BudgetTrendsPostgresTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Fact]
	public async Task BudgetTrends_TranslatesAndTotalsByEnvelopeAndOccasion()
	{
		// Arrange
		EnvelopeEntity groceries = new() { Id = Guid.NewGuid(), Name = "Budget groceries", IsActive = true };
		EnvelopeEntity archived = new() { Id = Guid.NewGuid(), Name = "Budget archived", IsActive = false, DeletedAt = DateTimeOffset.UtcNow };
		OccasionEntity party = new() { Id = Guid.NewGuid(), Name = "Budget party", IsActive = true };
		await using (var db = fixture.CreateDbContext())
		{
			db.Envelopes.AddRange(groceries, archived);
			db.Occasions.Add(party);
			ReceiptEntity january = Receipt(db, new(2019, 1, 31));
			ReceiptEntity march = Receipt(db, new(2019, 3, 1));
			Item(db, january, 10.10m, Currency.USD, groceries.Id, party.Id);
			Item(db, march, 5.05m, Currency.USD, groceries.Id, null);
			Item(db, january, 3.33m, Currency.USD, archived.Id, party.Id);
			Item(db, march, 2.22m, Currency.USD, null, null);
			Item(db, january, 7.77m, (Currency)1, archived.Id, null);
			// Excluded: deleted item, deleted receipt, outside the range.
			Item(db, january, 100m, Currency.USD, groceries.Id, party.Id).DeletedAt = DateTimeOffset.UtcNow;
			ReceiptEntity deleted = Receipt(db, new(2019, 2, 1));
			Item(db, deleted, 200m, Currency.USD, groceries.Id, party.Id);
			Item(db, Receipt(db, new(2019, 4, 1)), 300m, Currency.USD, groceries.Id, party.Id);
			await db.SaveChangesAsync();
			// A receipt admits no children once deleted, so delete it after its item is saved.
			deleted.DeletedAt = DateTimeOffset.UtcNow;
			await db.SaveChangesAsync();
		}
		var service = new ReportService(new Factory(fixture.CreateOptions()));

		// Act
		BudgetTrendsResult byEnvelope = await service.GetBudgetTrendsAsync(new(2019, 1, 1), new(2019, 3, 31), "monthly", 2,
			BudgetTrendsDimension.Envelope, null, null, default);
		BudgetTrendsResult byOccasion = await service.GetBudgetTrendsAsync(new(2019, 1, 1), new(2019, 3, 31), "quarterly", 1,
			BudgetTrendsDimension.Occasion, null, null, default);
		BudgetTrendsResult filtered = await service.GetBudgetTrendsAsync(new(2019, 1, 1), new(2019, 3, 31), "monthly", 7,
			BudgetTrendsDimension.Envelope, groceries.Id, party.Id, default);

		// Assert
		byEnvelope.Series.Should().Equal(
			new BudgetTrendsSeriesResult(archived.Id, "Budget archived", false, "1", false, 7.77m, 1),
			new BudgetTrendsSeriesResult(groceries.Id, "Budget groceries", true, "USD", false, 15.15m, 2),
			new BudgetTrendsSeriesResult(archived.Id, "Budget archived", false, "USD", false, 3.33m, 1),
			new BudgetTrendsSeriesResult(null, "Other", true, "USD", true, 2.22m, 1));
		byEnvelope.Buckets.Select(bucket => bucket.Period).Should().Equal("2019-01", "2019-02", "2019-03");
		byEnvelope.Buckets[0].Amounts.Should().Equal(7.77m, 10.10m, 3.33m, 0m);
		byEnvelope.Buckets[1].Amounts.Should().Equal(0m, 0m, 0m, 0m);
		byEnvelope.Buckets[2].Amounts.Should().Equal(0m, 5.05m, 0m, 2.22m);

		byOccasion.Series.Should().Equal(
			new BudgetTrendsSeriesResult(null, "No occasion", true, "1", false, 7.77m, 1),
			new BudgetTrendsSeriesResult(party.Id, "Budget party", true, "USD", false, 13.43m, 2),
			new BudgetTrendsSeriesResult(null, "Other", true, "USD", true, 7.27m, 2));
		byOccasion.Buckets.Should().ContainSingle().Which.Amounts.Should().Equal(7.77m, 13.43m, 7.27m);

		filtered.Series.Should().ContainSingle().Which.Should().Be(
			new BudgetTrendsSeriesResult(groceries.Id, "Budget groceries", true, "USD", false, 10.10m, 1));
	}

	private static ReceiptEntity Receipt(ApplicationDbContext db, DateOnly date)
	{
		var receipt = new ReceiptEntity { Id = Guid.NewGuid(), Date = date, Location = "Budget fixture" };
		db.Receipts.Add(receipt);
		return receipt;
	}

	private static ReceiptItemEntity Item(ApplicationDbContext db, ReceiptEntity receipt, decimal amount, Currency currency,
		Guid? envelopeId, Guid? occasionId)
	{
		var item = new ReceiptItemEntity
		{
			Id = Guid.NewGuid(),
			ReceiptId = receipt.Id,
			Description = "Budget purchase",
			Quantity = 1,
			UnitPrice = amount,
			TotalAmount = amount,
			TotalAmountCurrency = currency,
			UnitPriceCurrency = currency,
			EnvelopeId = envelopeId,
			OccasionId = occasionId
		};
		db.ReceiptItems.Add(item);
		return item;
	}

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
	}
}
