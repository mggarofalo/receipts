using Application.Models.Reports;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

// Postgres-only coverage for RECEIPTS-841: GetItemCostOverTimeAsync's query was rewritten from a
// .Join(...) chain into query-syntax with a LEFT JOIN to NormalizedDescriptions
// (`from n in normalizedJoin.DefaultIfEmpty()`, projecting CanonicalName = n.CanonicalName) so the
// new normalizedDescription filter has something to match on. The InMemory provider cannot prove
// this translates to SQL, and — more importantly — cannot prove the added LEFT JOIN doesn't
// duplicate or drop rows for the pre-existing description/category paths. Only a real Postgres
// connection can catch that regression.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ReportServiceItemCostOverTimeTests(PostgresFixture fixture)
{
	[Fact]
	public async Task GetItemCostOverTimeAsync_FiltersByNormalizedDescription_AcrossDifferentRawDescriptions()
	{
		// Arrange
		await ResetTablesAsync();

		Guid normalizedId = Guid.NewGuid();
		ReceiptEntity receipt1 = ReceiptEntityGenerator.Generate();
		receipt1.Date = new DateOnly(2025, 1, 10);
		ReceiptEntity receipt2 = ReceiptEntityGenerator.Generate();
		receipt2.Date = new DateOnly(2025, 2, 15);

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Milk",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			setup.Receipts.AddRange(receipt1, receipt2);

			ReceiptItemEntity item1 = ReceiptItemEntityGenerator.Generate(receipt1.Id);
			item1.Description = "2% Milk";
			item1.TotalAmount = 3.49m;
			item1.UnitPrice = 3.49m;
			item1.NormalizedDescriptionId = normalizedId;

			ReceiptItemEntity item2 = ReceiptItemEntityGenerator.Generate(receipt2.Id);
			item2.Description = "Skim Milk";
			item2.TotalAmount = 3.29m;
			item2.UnitPrice = 3.29m;
			item2.NormalizedDescriptionId = normalizedId;

			setup.ReceiptItems.AddRange(item1, item2);
			await setup.SaveChangesAsync();
		}

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act
		ItemCostOverTimeResult result = await service.GetItemCostOverTimeAsync(
			description: null, category: null, startDate: null, endDate: null, granularity: "exact",
			normalizedDescription: "Milk", CancellationToken.None);

		// Assert — both raw descriptions represented because they share the canonical name.
		result.Buckets.Should().HaveCount(2);
		result.Buckets.Select(b => b.Amount).Should().BeEquivalentTo([3.49m, 3.29m]);
	}

	[Fact]
	public async Task GetItemCostOverTimeAsync_NormalizedDescriptionFilter_ExcludesItemsWithNullFk()
	{
		// Arrange
		await ResetTablesAsync();

		Guid normalizedId = Guid.NewGuid();
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.Date = new DateOnly(2025, 1, 10);

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Milk",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			setup.Receipts.Add(receipt);

			ReceiptItemEntity linked = ReceiptItemEntityGenerator.Generate(receipt.Id);
			linked.Description = "Milk";
			linked.TotalAmount = 3.49m;
			linked.UnitPrice = 3.49m;
			linked.NormalizedDescriptionId = normalizedId;

			ReceiptItemEntity unlinked = ReceiptItemEntityGenerator.Generate(receipt.Id);
			unlinked.Description = "Milk (unlinked)";
			unlinked.TotalAmount = 99.99m;
			unlinked.UnitPrice = 99.99m;
			unlinked.NormalizedDescriptionId = null;

			setup.ReceiptItems.AddRange(linked, unlinked);
			await setup.SaveChangesAsync();
		}

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act
		ItemCostOverTimeResult result = await service.GetItemCostOverTimeAsync(
			description: null, category: null, startDate: null, endDate: null, granularity: "exact",
			normalizedDescription: "Milk", CancellationToken.None);

		// Assert — the unlinked item never matches, regardless of how close its description is.
		result.Buckets.Should().ContainSingle();
		result.Buckets[0].Amount.Should().Be(3.49m);
	}

	[Fact]
	public async Task GetItemCostOverTimeAsync_DescriptionFilter_StillReturnsExactPreLeftJoinResults()
	{
		// Arrange — regression guard: the LEFT JOIN added for normalizedDescription filtering must
		// not duplicate rows for items that DO have a normalized description, nor drop rows for items
		// that don't, when the caller filters by `description` instead.
		await ResetTablesAsync();

		Guid normalizedId = Guid.NewGuid();
		ReceiptEntity receipt1 = ReceiptEntityGenerator.Generate();
		receipt1.Date = new DateOnly(2025, 1, 10);
		ReceiptEntity receipt2 = ReceiptEntityGenerator.Generate();
		receipt2.Date = new DateOnly(2025, 2, 15);

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Milk",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			setup.Receipts.AddRange(receipt1, receipt2);

			// Same raw description ("Milk"): one item is linked to a normalized description, one is not.
			// A LEFT JOIN that fans out incorrectly would turn either row into more than one result.
			ReceiptItemEntity linkedMilk = ReceiptItemEntityGenerator.Generate(receipt1.Id);
			linkedMilk.Description = "Milk";
			linkedMilk.TotalAmount = 3.49m;
			linkedMilk.UnitPrice = 3.49m;
			linkedMilk.NormalizedDescriptionId = normalizedId;

			ReceiptItemEntity unlinkedMilk = ReceiptItemEntityGenerator.Generate(receipt2.Id);
			unlinkedMilk.Description = "Milk";
			unlinkedMilk.TotalAmount = 3.29m;
			unlinkedMilk.UnitPrice = 3.29m;
			unlinkedMilk.NormalizedDescriptionId = null;

			setup.ReceiptItems.AddRange(linkedMilk, unlinkedMilk);
			await setup.SaveChangesAsync();
		}

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act
		ItemCostOverTimeResult result = await service.GetItemCostOverTimeAsync(
			description: "Milk", category: null, startDate: null, endDate: null, granularity: "exact",
			normalizedDescription: null, CancellationToken.None);

		// Assert — exactly the two "Milk" items, no duplication and no drops from the added join.
		result.Buckets.Should().HaveCount(2);
		result.Buckets.Select(b => b.Amount).Should().BeEquivalentTo([3.49m, 3.29m]);
	}

	[Fact]
	public async Task GetItemCostOverTimeAsync_CategoryFilter_StillReturnsExactPreLeftJoinResults()
	{
		// Arrange — same regression guard as the description case, but for the category path, which
		// sits behind both description and normalizedDescription in the precedence chain.
		await ResetTablesAsync();

		Guid normalizedId = Guid.NewGuid();
		ReceiptEntity receipt1 = ReceiptEntityGenerator.Generate();
		receipt1.Date = new DateOnly(2025, 1, 10);
		ReceiptEntity receipt2 = ReceiptEntityGenerator.Generate();
		receipt2.Date = new DateOnly(2025, 2, 15);
		ReceiptEntity receipt3 = ReceiptEntityGenerator.Generate();
		receipt3.Date = new DateOnly(2025, 3, 20);

		await using (ApplicationDbContext setup = fixture.CreateDbContext())
		{
			setup.NormalizedDescriptions.Add(new NormalizedDescriptionEntity
			{
				Id = normalizedId,
				CanonicalName = "Milk",
				Status = NormalizedDescriptionStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			setup.Receipts.AddRange(receipt1, receipt2, receipt3);

			ReceiptItemEntity dairyLinked = ReceiptItemEntityGenerator.Generate(receipt1.Id);
			dairyLinked.Description = "Milk";
			dairyLinked.Category = "Dairy";
			dairyLinked.TotalAmount = 3.49m;
			dairyLinked.UnitPrice = 3.49m;
			dairyLinked.NormalizedDescriptionId = normalizedId;

			ReceiptItemEntity dairyUnlinked = ReceiptItemEntityGenerator.Generate(receipt2.Id);
			dairyUnlinked.Description = "Cheese";
			dairyUnlinked.Category = "Dairy";
			dairyUnlinked.TotalAmount = 5.99m;
			dairyUnlinked.UnitPrice = 5.99m;
			dairyUnlinked.NormalizedDescriptionId = null;

			// Different category entirely — must never appear in a "Dairy" result.
			ReceiptItemEntity produce = ReceiptItemEntityGenerator.Generate(receipt3.Id);
			produce.Description = "Bananas";
			produce.Category = "Produce";
			produce.TotalAmount = 1.50m;
			produce.UnitPrice = 1.50m;
			produce.NormalizedDescriptionId = null;

			setup.ReceiptItems.AddRange(dairyLinked, dairyUnlinked, produce);
			await setup.SaveChangesAsync();
		}

		ReportService service = new(new FixtureDbContextFactory(fixture));

		// Act
		ItemCostOverTimeResult result = await service.GetItemCostOverTimeAsync(
			description: null, category: "Dairy", startDate: null, endDate: null, granularity: "exact",
			normalizedDescription: null, CancellationToken.None);

		// Assert — both Dairy items, none duplicated, Produce excluded.
		result.Buckets.Should().HaveCount(2);
		result.Buckets.Select(b => b.Amount).Should().BeEquivalentTo([3.49m, 5.99m]);
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
