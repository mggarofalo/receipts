using Application.Models.Reports;
using Common;
using FluentAssertions;
using Infrastructure.Configurations;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class CategoryTrendsPostgresTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	[Fact]
	public async Task Trends_GroupsMultiplePurchasePeriods_WithInclusiveBoundsAndCurrencyLocalOther()
	{
		Guid revision = Guid.NewGuid(), primary = Guid.NewGuid(), secondary = Guid.NewGuid();
		DateOnly[] dates = [new(2020, 2, 27), new(2020, 2, 28), new(2020, 2, 29),
			new(2020, 3, 1), new(2020, 3, 31), new(2020, 4, 1), new(2021, 1, 1), new(2021, 1, 2)];
		await using (var db = fixture.CreateDbContext())
		{
			await using var transaction = await db.Database.BeginTransactionAsync();
			db.TaxonomyIdentities.AddRange(new TaxonomyIdentityEntity { Id = primary }, new TaxonomyIdentityEntity { Id = secondary });
			db.TaxonomyRevisions.Add(new() { Id = revision, Sequence = 1, PreviousRevisionId = TaxonomyConfiguration.GenesisId });
			db.TaxonomyNodes.AddRange(new TaxonomyNodeEntity { RevisionId = revision, CategoryId = primary, Name = "Same label" },
				new TaxonomyNodeEntity { RevisionId = revision, CategoryId = secondary, Name = "Secondary label" });
			(await db.TaxonomyHeads.SingleAsync()).RevisionId = revision;
			foreach (var date in dates)
			{
				var receipt = new ReceiptEntity { Id = Guid.NewGuid(), Date = date, Location = "Boundary fixture" };
				db.Receipts.Add(receipt);
				// Rank opposite identities in each currency; both currencies require their own Other.
				foreach (var (category, currency, amount) in new[] {
					(primary, Currency.USD, 10.12m), (secondary, Currency.USD, 1.01m),
					(primary, (Currency)1, 2.02m), (secondary, (Currency)1, 20.23m) })
				{
					var item = new ReceiptItemEntity
					{
						Id = Guid.NewGuid(),
						ReceiptId = receipt.Id,
						Description = "Boundary purchase",
						Quantity = 1,
						UnitPrice = amount,
						TotalAmount = amount,
						TotalAmountCurrency = currency,
						UnitPriceCurrency = currency
					};
					var assignment = new PurchaseClassificationEntity
					{
						Id = Guid.NewGuid(),
						ReceiptItemId = item.Id,
						Sequence = 1,
						CategoryId = category,
						TaxonomyRevisionId = revision,
						RecordedAt = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)
					};
					db.ReceiptItems.Add(item);
					db.Classifications.Add(assignment);
					db.CurrentClassifications.Add(new() { ReceiptItemId = item.Id, AssignmentId = assignment.Id });
				}
			}
			await db.SaveChangesAsync();
			await transaction.CommitAsync();
		}

		var service = new ReportService(new Factory(fixture.CreateOptions()));
		var expected = new Dictionary<string, Dictionary<string, int>>
		{
			["daily"] = new()
			{
				["2020-02-28"] = 1,
				["2020-02-29"] = 1,
				["2020-03-01"] = 1,
				["2020-03-31"] = 1,
				["2020-04-01"] = 1,
				["2021-01-01"] = 1
			},
			["monthly"] = new() { ["2020-02"] = 2, ["2020-03"] = 2, ["2020-04"] = 1, ["2021-01"] = 1 },
			["quarterly"] = new() { ["2020 Q1"] = 4, ["2020 Q2"] = 1, ["2021 Q1"] = 1 },
			["yearly"] = new() { ["2020"] = 5, ["2021"] = 1 }
		};
		foreach (var (granularity, periods) in expected)
		{
			foreach (var view in Enum.GetValues<CategoryReportView>())
			{
				foreach (DateTimeOffset? cutoff in new DateTimeOffset?[] { null, new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero) })
				{
					var result = await service.GetCategoryTrendsAsync(new(2020, 2, 28), new(2021, 1, 1), granularity, 1, default,
						new(view, KnowledgeCutoff: cutoff, CapturedCurrentRevisionId: view == CategoryReportView.CurrentHierarchy ? revision : null));
					result.Series.Should().HaveCount(4);
					result.Series.Count(series => series.IsOther).Should().Be(2);
					result.Snapshot!.Buckets.Sum(bucket => bucket.PurchaseCount).Should().Be(24);
					result.Snapshot.AssignmentSelection.Should().Be(cutoff.HasValue ? CategoryReportAssignmentSelection.LatestRecordedAtOrBeforeCutoff : CategoryReportAssignmentSelection.CurrentPointer);
					result.Series.Single(series => !series.IsOther && series.Currency == "USD").Bucket!.CategoryId.Should().Be(primary);
					result.Series.Single(series => !series.IsOther && series.Currency == "1").Bucket!.CategoryId.Should().Be(secondary);
					result.Buckets.Where(bucket => bucket.Amounts.Any(amount => amount != 0)).Select(bucket => bucket.Period)
						.Should().BeEquivalentTo(periods.Keys);
					foreach (var bucket in result.Buckets)
					{
						int multiplier = periods.GetValueOrDefault(bucket.Period);
						for (int i = 0; i < result.Series.Count; i++)
						{
							var series = result.Series[i];
							decimal unit = series.Currency == "USD" ? series.IsOther ? 1.01m : 10.12m : series.IsOther ? 2.02m : 20.23m;
							bucket.Amounts[i].Should().Be(unit * multiplier, $"{view}/{granularity}/{bucket.Period}/{series.Currency}/{series.IsOther}");
						}
					}
				}
			}
		}
	}

	private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
	}
}
