using Application.Models.Reports;
using Common;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

// Postgres-only coverage for RECEIPTS-841: GetSpendingByNormalizedDescriptionAsync now groups,
// sorts, and paginates entirely in SQL (GROUP BY canonical-name-or-"(Not Normalized)", ORDER BY,
// OFFSET/LIMIT) and runs a second page-scoped query for dominant-currency resolution. The InMemory
// unit suite client-evaluates all of this, so it can never prove the LINQ actually translates —
// only a real Postgres connection can catch a translation regression.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ReportServiceSpendingByNormalizedDescriptionTests(PostgresFixture fixture)
{
	[Fact]
	public async Task GetSpendingByNormalizedDescriptionAsync_AggregatesByCanonicalName_AndBucketsNullFk()
	{
		// Arrange
		await ResetTablesAsync();

		Guid normalizedId = Guid.NewGuid();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Organic Milk",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});

			setup.Receipts.Add(receipt);

			ReceiptItemEntity linked1 = ReceiptItemEntityGenerator.Generate(receipt.Id);
			linked1.Description = "organic milk";
			linked1.TotalAmount = 4.00m;
			linked1.NormalizedDescriptionId = normalizedId;

			ReceiptItemEntity linked2 = ReceiptItemEntityGenerator.Generate(receipt.Id);
			linked2.Description = "ORGANIC MILK";
			linked2.TotalAmount = 5.50m;
			linked2.NormalizedDescriptionId = normalizedId;

			ReceiptItemEntity unlinked = ReceiptItemEntityGenerator.Generate(receipt.Id);
			unlinked.Description = "mystery item";
			unlinked.TotalAmount = 2.00m;
			unlinked.NormalizedDescriptionId = null;

			setup.ReceiptItems.AddRange(linked1, linked2, unlinked);
			await setup.SaveChangesAsync();
		}

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act
		SpendingByNormalizedDescriptionResult result = await service
			.GetSpendingByNormalizedDescriptionAsync(from: null, to: null, "totalAmount", "desc", 1, 50, CancellationToken.None);

		// Assert
		result.Items.Should().HaveCount(2);
		result.TotalCount.Should().Be(2);
		result.GrandTotal.Should().Be(11.50m);

		SpendingByNormalizedDescriptionItem milk = result.Items.Single(i => i.CanonicalName == "Organic Milk");
		milk.TotalAmount.Should().Be(9.50m);
		milk.ItemCount.Should().Be(2);
		milk.Currency.Should().Be("USD");
		milk.FirstSeen.Should().NotBeNull();
		milk.LastSeen.Should().NotBeNull();

		SpendingByNormalizedDescriptionItem notNormalized = result.Items.Single(i => i.CanonicalName == "(Not Normalized)");
		notNormalized.TotalAmount.Should().Be(2.00m);
		notNormalized.ItemCount.Should().Be(1);

		milk.Status.Should().Be(NormalizedDescriptionStatus.Active);
		// No backing row to carry a status. Null here is what stops the client rendering the
		// synthetic bucket as either reviewed or unreviewed (RECEIPTS-875).
		notNormalized.Status.Should().BeNull();
	}

	// RECEIPTS-875: the report is where approval becomes observable, so a bucket whose canonical
	// row is still PendingReview has to arrive marked. The status comes from a second page-scoped
	// query rather than an extra GROUP BY key — a translation regression there would silently
	// return null for every bucket and quietly un-gate the review queue again.
	[Fact]
	public async Task GetSpendingByNormalizedDescriptionAsync_CarriesPendingReviewStatus()
	{
		// Arrange
		await ResetTablesAsync();

		Guid activeId = Guid.NewGuid();
		Guid pendingId = Guid.NewGuid();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.NormalizedDescriptions.AddRange(
				new NormalizedDescriptionEntity
				{
					Id = activeId,
					CanonicalName = "Settled Item",
					Status = NormalizedDescriptionStatus.Active,
					CreatedAt = DateTimeOffset.UtcNow,
				},
				new NormalizedDescriptionEntity
				{
					Id = pendingId,
					CanonicalName = "Unconfirmed Item",
					Status = NormalizedDescriptionStatus.PendingReview,
					CreatedAt = DateTimeOffset.UtcNow,
				});

			setup.Receipts.Add(receipt);

			ReceiptItemEntity settled = ReceiptItemEntityGenerator.Generate(receipt.Id);
			settled.Description = "settled item";
			settled.TotalAmount = 3.00m;
			settled.NormalizedDescriptionId = activeId;

			ReceiptItemEntity unconfirmed = ReceiptItemEntityGenerator.Generate(receipt.Id);
			unconfirmed.Description = "unconfirmed item";
			unconfirmed.TotalAmount = 7.00m;
			unconfirmed.NormalizedDescriptionId = pendingId;

			setup.ReceiptItems.AddRange(settled, unconfirmed);
			await setup.SaveChangesAsync();
		}

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act
		SpendingByNormalizedDescriptionResult result = await service
			.GetSpendingByNormalizedDescriptionAsync(from: null, to: null, "totalAmount", "desc", 1, 50, CancellationToken.None);

		// Assert
		result.Items.Single(i => i.CanonicalName == "Settled Item")
			.Status.Should().Be(NormalizedDescriptionStatus.Active);
		result.Items.Single(i => i.CanonicalName == "Unconfirmed Item")
			.Status.Should().Be(NormalizedDescriptionStatus.PendingReview);

		// The unreviewed bucket's money still counts. A report that silently drops it would stop
		// reconciling against receipt totals, which is the reason pending buckets stay visible
		// rather than being filtered out.
		result.GrandTotal.Should().Be(10.00m);
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescriptionAsync_FiltersByDateRange()
	{
		// Arrange
		await ResetTablesAsync();

		Guid normalizedId = Guid.NewGuid();

		ReceiptEntity receiptInRange = ReceiptEntityGenerator.Generate();
		receiptInRange.Date = new DateOnly(2025, 6, 15);

		ReceiptEntity receiptOutOfRange = ReceiptEntityGenerator.Generate();
		receiptOutOfRange.Date = new DateOnly(2024, 1, 1);

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Bananas",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			setup.Receipts.AddRange(receiptInRange, receiptOutOfRange);

			ReceiptItemEntity inRange = ReceiptItemEntityGenerator.Generate(receiptInRange.Id);
			inRange.Description = "bananas";
			inRange.TotalAmount = 1.50m;
			inRange.NormalizedDescriptionId = normalizedId;

			ReceiptItemEntity outOfRange = ReceiptItemEntityGenerator.Generate(receiptOutOfRange.Id);
			outOfRange.Description = "bananas";
			outOfRange.TotalAmount = 99.99m;
			outOfRange.NormalizedDescriptionId = normalizedId;

			setup.ReceiptItems.AddRange(inRange, outOfRange);
			await setup.SaveChangesAsync();
		}

		ReportService service = new(new FixtureDbContextFactory(fixture));

		DateTimeOffset from = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
		DateTimeOffset to = new(2025, 12, 31, 0, 0, 0, TimeSpan.Zero);

		// Act
		SpendingByNormalizedDescriptionResult result = await service
			.GetSpendingByNormalizedDescriptionAsync(from, to, "totalAmount", "desc", 1, 50, CancellationToken.None);

		// Assert — only the receipt within the range contributed
		result.Items.Should().ContainSingle();
		result.Items[0].CanonicalName.Should().Be("Bananas");
		result.Items[0].TotalAmount.Should().Be(1.50m);
		result.Items[0].ItemCount.Should().Be(1);
		result.FromDate.Should().Be(from);
		result.ToDate.Should().Be(to);
		result.TotalCount.Should().Be(1);
		result.GrandTotal.Should().Be(1.50m);
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescriptionAsync_IgnoresSoftDeletedItemsAndReceipts()
	{
		// Arrange
		await ResetTablesAsync();

		Guid normalizedId = Guid.NewGuid();
		ReceiptEntity live = ReceiptEntityGenerator.Generate();
		ReceiptEntity deleted = ReceiptEntityGenerator.Generate();
		deleted.DeletedAt = DateTimeOffset.UtcNow;

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Eggs",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});

			setup.Receipts.AddRange(live, deleted);

			ReceiptItemEntity liveItem = ReceiptItemEntityGenerator.Generate(live.Id);
			liveItem.Description = "eggs";
			liveItem.TotalAmount = 3.00m;
			liveItem.NormalizedDescriptionId = normalizedId;

			ReceiptItemEntity softDeletedItem = ReceiptItemEntityGenerator.Generate(live.Id);
			softDeletedItem.Description = "eggs";
			softDeletedItem.TotalAmount = 100.00m;
			softDeletedItem.NormalizedDescriptionId = normalizedId;
			softDeletedItem.DeletedAt = DateTimeOffset.UtcNow;

			ReceiptItemEntity itemOnDeletedReceipt = ReceiptItemEntityGenerator.Generate(deleted.Id);
			itemOnDeletedReceipt.Description = "eggs";
			itemOnDeletedReceipt.TotalAmount = 50.00m;
			itemOnDeletedReceipt.NormalizedDescriptionId = normalizedId;

			setup.ReceiptItems.AddRange(liveItem, softDeletedItem, itemOnDeletedReceipt);
			await setup.SaveChangesAsync();
		}

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act
		SpendingByNormalizedDescriptionResult result = await service
			.GetSpendingByNormalizedDescriptionAsync(null, null, "totalAmount", "desc", 1, 50, CancellationToken.None);

		// Assert — only the live item on the live receipt counted
		result.Items.Should().ContainSingle();
		result.Items[0].CanonicalName.Should().Be("Eggs");
		result.Items[0].TotalAmount.Should().Be(3.00m);
		result.Items[0].ItemCount.Should().Be(1);
	}

	[Theory]
	[InlineData("asc")]
	[InlineData("desc")]
	public async Task GetSpendingByNormalizedDescriptionAsync_SortsByCanonicalName_InSql(string direction)
	{
		// Arrange
		await ResetTablesAsync();
		await SeedThreeBucketsAsync();

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act
		SpendingByNormalizedDescriptionResult result = await service
			.GetSpendingByNormalizedDescriptionAsync(null, null, "canonicalName", direction, 1, 50, CancellationToken.None);

		// Assert
		List<string> expected = direction == "asc"
			? ["Apples", "Bananas", "Cherries"]
			: ["Cherries", "Bananas", "Apples"];
		result.Items.Select(i => i.CanonicalName).Should().Equal(expected);
	}

	[Theory]
	[InlineData("asc")]
	[InlineData("desc")]
	public async Task GetSpendingByNormalizedDescriptionAsync_SortsByItemCount_InSql(string direction)
	{
		// Arrange — Apples: 1 item, Cherries: 2 items, Bananas: 3 items.
		await ResetTablesAsync();
		await SeedThreeBucketsAsync();

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act
		SpendingByNormalizedDescriptionResult result = await service
			.GetSpendingByNormalizedDescriptionAsync(null, null, "itemCount", direction, 1, 50, CancellationToken.None);

		// Assert
		List<string> expected = direction == "asc"
			? ["Apples", "Cherries", "Bananas"]
			: ["Bananas", "Cherries", "Apples"];
		result.Items.Select(i => i.CanonicalName).Should().Equal(expected);
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescriptionAsync_PaginatesInSql_WithNoGapsOrDuplicates_WhenTotalsTie()
	{
		// Arrange — five buckets that all tie on totalAmount, forcing the ThenBy(CanonicalName)
		// tiebreaker to keep offset pagination stable and gap-free across page requests.
		await ResetTablesAsync();

		string[] names = ["Alpha", "Bravo", "Charlie", "Delta", "Echo"];

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			setup.Receipts.Add(receipt);

			foreach (string name in names)
			{
				Guid normalizedId = Guid.NewGuid();
				setup.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
				{
					Id = normalizedId,
					CanonicalName = name,
					Status = NormalizedDescriptionStatus.Active,
					CreatedAt = DateTimeOffset.UtcNow,
				});

				ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
				item.Description = name;
				item.TotalAmount = 10.00m; // tie across every bucket
				item.NormalizedDescriptionId = normalizedId;
				setup.ReceiptItems.Add(item);
			}

			await setup.SaveChangesAsync();
		}

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act — page through with pageSize=2, collecting every row returned.
		List<string> collected = [];
		for (int page = 1; page <= 3; page++)
		{
			SpendingByNormalizedDescriptionResult pageResult = await service
				.GetSpendingByNormalizedDescriptionAsync(null, null, "totalAmount", "desc", page, 2, CancellationToken.None);
			collected.AddRange(pageResult.Items.Select(i => i.CanonicalName));
			pageResult.TotalCount.Should().Be(5);
		}

		// Assert — every bucket appeared exactly once, in the deterministic tiebreak order
		// (totalAmount desc is all-tied, so ThenBy(CanonicalName) ascending decides the order).
		collected.Should().Equal(names);
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescriptionAsync_TotalCountAndGrandTotal_SpanAllBuckets_NotJustRequestedPage()
	{
		// Arrange
		await ResetTablesAsync();

		(string Name, decimal Total)[] buckets =
		[
			("Apples", 10.00m),
			("Bananas", 20.00m),
			("Cherries", 30.00m),
			("Dates", 40.00m),
			("Elderberries", 50.00m),
		];

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			setup.Receipts.Add(receipt);

			foreach ((string name, decimal total) in buckets)
			{
				Guid normalizedId = Guid.NewGuid();
				setup.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
				{
					Id = normalizedId,
					CanonicalName = name,
					Status = NormalizedDescriptionStatus.Active,
					CreatedAt = DateTimeOffset.UtcNow,
				});

				ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
				item.Description = name;
				item.TotalAmount = total;
				item.NormalizedDescriptionId = normalizedId;
				setup.ReceiptItems.Add(item);
			}

			await setup.SaveChangesAsync();
		}

		ReportService service = new(new FixtureDbContextFactory(fixture));
		decimal expectedGrandTotal = buckets.Sum(b => b.Total);

		// Act — request only a 2-row page out of 5 buckets.
		SpendingByNormalizedDescriptionResult result = await service
			.GetSpendingByNormalizedDescriptionAsync(null, null, "totalAmount", "desc", 1, 2, CancellationToken.None);

		// Assert
		result.Items.Should().HaveCount(2, "only the requested page should be materialized");
		result.TotalCount.Should().Be(5, "TotalCount is the number of buckets, not the page size");
		result.GrandTotal.Should().Be(expectedGrandTotal, "GrandTotal must span every bucket, not just the returned page");
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescriptionAsync_NotNormalizedBucket_AppearsAndPaginatesAlongsideRealNames()
	{
		// Arrange — one real canonical name plus a group of items with no NormalizedDescriptionId,
		// which must bucket into the synthetic "(Not Normalized)" group via COALESCE and remain
		// sortable/paginatable alongside real names rather than being dropped or NULL-ordered away.
		await ResetTablesAsync();

		Guid normalizedId = Guid.NewGuid();

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			setup.Receipts.Add(receipt);

			setup.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Apples",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});

			ReceiptItemEntity normalizedItem = ReceiptItemEntityGenerator.Generate(receipt.Id);
			normalizedItem.Description = "apples";
			normalizedItem.TotalAmount = 5.00m;
			normalizedItem.NormalizedDescriptionId = normalizedId;

			ReceiptItemEntity unnormalizedItem1 = ReceiptItemEntityGenerator.Generate(receipt.Id);
			unnormalizedItem1.Description = "mystery item 1";
			unnormalizedItem1.TotalAmount = 3.00m;
			unnormalizedItem1.NormalizedDescriptionId = null;

			ReceiptItemEntity unnormalizedItem2 = ReceiptItemEntityGenerator.Generate(receipt.Id);
			unnormalizedItem2.Description = "mystery item 2";
			unnormalizedItem2.TotalAmount = 7.00m;
			unnormalizedItem2.NormalizedDescriptionId = null;

			setup.ReceiptItems.AddRange(normalizedItem, unnormalizedItem1, unnormalizedItem2);
			await setup.SaveChangesAsync();
		}

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act — page size 1 forces the synthetic bucket through the same paginated ORDER BY as a
		// real canonical name, proving it is not skipped or duplicated by the OFFSET/LIMIT.
		List<string> collected = [];
		int totalCount = 0;
		for (int page = 1; page <= 2; page++)
		{
			SpendingByNormalizedDescriptionResult pageResult = await service
				.GetSpendingByNormalizedDescriptionAsync(null, null, "totalAmount", "desc", page, 1, CancellationToken.None);
			collected.AddRange(pageResult.Items.Select(i => i.CanonicalName));
			totalCount = pageResult.TotalCount;
		}

		// Assert
		totalCount.Should().Be(2);
		collected.Should().BeEquivalentTo(["Apples", "(Not Normalized)"]);

		SpendingByNormalizedDescriptionResult fullResult = await service
			.GetSpendingByNormalizedDescriptionAsync(null, null, "totalAmount", "desc", 1, 50, CancellationToken.None);
		SpendingByNormalizedDescriptionItem notNormalized = fullResult.Items.Single(i => i.CanonicalName == "(Not Normalized)");
		notNormalized.TotalAmount.Should().Be(10.00m);
		notNormalized.ItemCount.Should().Be(2);
	}

	[Fact]
	public async Task GetSpendingByNormalizedDescriptionAsync_ResolvesDominantCurrency_ViaPageScopedQuery()
	{
		// Arrange — GetDominantCurrenciesAsync runs as a second, page-scoped query (see
		// ReportService.GetDominantCurrenciesAsync). This proves that query translates and resolves
		// correctly for a multi-item bucket. NOTE: Common.Currency currently defines only USD, so a
		// true mixed-currency tie-break cannot be constructed against the real enum today — this
		// exercises the resolution path (multiple items, one currency) rather than an actual tie.
		await ResetTablesAsync();

		Guid normalizedId = Guid.NewGuid();

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			setup.Receipts.Add(receipt);

			setup.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Coffee",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});

			ReceiptItemEntity item1 = ReceiptItemEntityGenerator.Generate(receipt.Id);
			item1.Description = "coffee";
			item1.TotalAmount = 4.00m;
			item1.TotalAmountCurrency = Currency.USD;
			item1.NormalizedDescriptionId = normalizedId;

			ReceiptItemEntity item2 = ReceiptItemEntityGenerator.Generate(receipt.Id);
			item2.Description = "coffee";
			item2.TotalAmount = 6.00m;
			item2.TotalAmountCurrency = Currency.USD;
			item2.NormalizedDescriptionId = normalizedId;

			setup.ReceiptItems.AddRange(item1, item2);
			await setup.SaveChangesAsync();
		}

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act
		SpendingByNormalizedDescriptionResult result = await service
			.GetSpendingByNormalizedDescriptionAsync(null, null, "totalAmount", "desc", 1, 50, CancellationToken.None);

		// Assert
		SpendingByNormalizedDescriptionItem coffee = result.Items.Single(i => i.CanonicalName == "Coffee");
		coffee.Currency.Should().Be("USD");
		coffee.TotalAmount.Should().Be(10.00m);
		coffee.ItemCount.Should().Be(2);
	}

	// Seeds three canonical-name buckets with distinct item counts and totals for sort assertions:
	// Apples (1 item, $30), Bananas (3 items, $10 total), Cherries (2 items, $20 total).
	private async Task SeedThreeBucketsAsync()
	{
		await using ApplicationDbContext setup = fixture.CreateDbContext();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		setup.Receipts.Add(receipt);

		Guid applesId = Guid.NewGuid();
		Guid bananasId = Guid.NewGuid();
		Guid cherriesId = Guid.NewGuid();

		setup.NormalizedDescriptions.AddRange(
			new NormalizedDescriptionEntity { Id = applesId, CanonicalName = "Apples", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow },
			new NormalizedDescriptionEntity { Id = bananasId, CanonicalName = "Bananas", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow },
			new NormalizedDescriptionEntity { Id = cherriesId, CanonicalName = "Cherries", Status = NormalizedDescriptionStatus.Active, CreatedAt = DateTimeOffset.UtcNow });

		ReceiptItemEntity apple = ReceiptItemEntityGenerator.Generate(receipt.Id);
		apple.Description = "apple";
		apple.TotalAmount = 30.00m;
		apple.NormalizedDescriptionId = applesId;
		setup.ReceiptItems.Add(apple);

		for (int i = 0; i < 3; i++)
		{
			ReceiptItemEntity banana = ReceiptItemEntityGenerator.Generate(receipt.Id);
			banana.Description = "banana";
			banana.TotalAmount = 3.33m;
			banana.NormalizedDescriptionId = bananasId;
			setup.ReceiptItems.Add(banana);
		}

		for (int i = 0; i < 2; i++)
		{
			ReceiptItemEntity cherry = ReceiptItemEntityGenerator.Generate(receipt.Id);
			cherry.Description = "cherry";
			cherry.TotalAmount = 10.00m;
			cherry.NormalizedDescriptionId = cherriesId;
			setup.ReceiptItems.Add(cherry);
		}

		await setup.SaveChangesAsync();
	}

	private async Task ResetTablesAsync()
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		await context.Database.ExecuteSqlRawAsync(
			"""TRUNCATE "ReceiptItems", "Receipts", "NormalizedDescriptions", "DistinctDescriptions" RESTART IDENTITY CASCADE;""");
	}

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
