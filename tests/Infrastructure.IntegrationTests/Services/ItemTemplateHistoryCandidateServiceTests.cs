using Application.Models;
using Application.Queries.Core.ItemTemplate.GetHistoryCandidates;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

/// <summary>
/// Pins the history-candidate aggregation SQL against a real Postgres instance: case-insensitive
/// grouping, exclusion of descriptions that already have a template, the minCount floor, and the
/// selection rules for each suggested field. The SQL uses DISTINCT ON and schema-qualified tables,
/// so none of it is exercisable through the InMemory provider.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ItemTemplateHistoryCandidateServiceTests(PostgresFixture fixture)
{
	[Fact]
	public async Task GetHistoryCandidatesAsync_GroupsDescriptionsCaseInsensitively()
	{
		// Arrange
		await ResetAsync();
		ReceiptEntity older = Receipt(new DateOnly(2026, 1, 10));
		ReceiptEntity newer = Receipt(new DateOnly(2026, 3, 20));

		await SeedAsync(
			[older, newer],
			[
				Item(older.Id, "whole milk"),
				Item(older.Id, "WHOLE MILK"),
				Item(newer.Id, "Whole Milk"),
			]);

		// Act
		PagedResult<ItemTemplateHistoryCandidate> result = await CreateService().GetHistoryCandidatesAsync(0, 50, 2, CancellationToken.None);

		// Assert — one candidate, named with the casing from the most recent receipt
		result.Total.Should().Be(1);
		ItemTemplateHistoryCandidate candidate = result.Data.Single();
		candidate.Name.Should().Be("Whole Milk");
		candidate.OccurrenceCount.Should().Be(3);
		candidate.LastPurchasedAt.Should().Be(new DateOnly(2026, 3, 20));
	}

	[Fact]
	public async Task GetHistoryCandidatesAsync_ExcludesDescriptionsMatchingAnExistingTemplate()
	{
		// Arrange
		await ResetAsync();
		ReceiptEntity receipt = Receipt(new DateOnly(2026, 2, 1));

		await SeedAsync(
			[receipt],
			[
				Item(receipt.Id, "Whole Milk"),
				Item(receipt.Id, "Whole Milk"),
				Item(receipt.Id, "Sourdough Bread"),
				Item(receipt.Id, "Sourdough Bread"),
			],
			// Casing differs from the receipt items on purpose — the match must be case-insensitive.
			[Template("WHOLE MILK")]);

		// Act
		PagedResult<ItemTemplateHistoryCandidate> result = await CreateService().GetHistoryCandidatesAsync(0, 50, 2, CancellationToken.None);

		// Assert
		result.Total.Should().Be(1);
		result.Data.Single().Name.Should().Be("Sourdough Bread");
	}

	[Fact]
	public async Task GetHistoryCandidatesAsync_IgnoresSoftDeletedTemplates()
	{
		// Arrange
		await ResetAsync();
		ReceiptEntity receipt = Receipt(new DateOnly(2026, 2, 1));

		ItemTemplateEntity deletedTemplate = Template("Whole Milk");
		deletedTemplate.DeletedAt = DateTimeOffset.UtcNow;

		await SeedAsync(
			[receipt],
			[
				Item(receipt.Id, "Whole Milk"),
				Item(receipt.Id, "Whole Milk"),
			],
			[deletedTemplate]);

		// Act
		PagedResult<ItemTemplateHistoryCandidate> result = await CreateService().GetHistoryCandidatesAsync(0, 50, 2, CancellationToken.None);

		// Assert — a soft-deleted template must not suppress the candidate
		result.Data.Should().ContainSingle(c => c.Name == "Whole Milk");
	}

	[Fact]
	public async Task GetHistoryCandidatesAsync_AppliesMinCountFloor()
	{
		// Arrange
		await ResetAsync();
		ReceiptEntity receipt = Receipt(new DateOnly(2026, 2, 1));

		await SeedAsync(
			[receipt],
			[
				Item(receipt.Id, "Coffee Beans"),
				Item(receipt.Id, "Coffee Beans"),
				Item(receipt.Id, "Coffee Beans"),
				Item(receipt.Id, "Paper Towels"),
			]);

		// Act
		PagedResult<ItemTemplateHistoryCandidate> atLeastTwo = await CreateService().GetHistoryCandidatesAsync(0, 50, 2, CancellationToken.None);
		PagedResult<ItemTemplateHistoryCandidate> atLeastThree = await CreateService().GetHistoryCandidatesAsync(0, 50, 3, CancellationToken.None);
		PagedResult<ItemTemplateHistoryCandidate> atLeastOne = await CreateService().GetHistoryCandidatesAsync(0, 50, 1, CancellationToken.None);

		// Assert
		atLeastTwo.Data.Select(c => c.Name).Should().BeEquivalentTo(["Coffee Beans"]);
		atLeastThree.Data.Select(c => c.Name).Should().BeEquivalentTo(["Coffee Beans"]);
		atLeastOne.Data.Select(c => c.Name).Should().BeEquivalentTo(["Coffee Beans", "Paper Towels"]);
	}

	[Fact]
	public async Task GetHistoryCandidatesAsync_ExcludesSoftDeletedReceiptItemsAndReceipts()
	{
		// Arrange
		await ResetAsync();
		ReceiptEntity live = Receipt(new DateOnly(2026, 2, 1));
		ReceiptEntity deletedReceipt = Receipt(new DateOnly(2026, 2, 2));
		deletedReceipt.DeletedAt = DateTimeOffset.UtcNow;

		ReceiptItemEntity deletedItem = Item(live.Id, "Coffee Beans");
		deletedItem.DeletedAt = DateTimeOffset.UtcNow;

		await SeedAsync(
			[live, deletedReceipt],
			[
				Item(live.Id, "Coffee Beans"),
				Item(live.Id, "Coffee Beans"),
				deletedItem,
				Item(deletedReceipt.Id, "Coffee Beans"),
			]);

		// Act
		PagedResult<ItemTemplateHistoryCandidate> result = await CreateService().GetHistoryCandidatesAsync(0, 50, 2, CancellationToken.None);

		// Assert — only the two live items on the live receipt count
		result.Data.Single().OccurrenceCount.Should().Be(2);
	}

	[Fact]
	public async Task GetHistoryCandidatesAsync_SuggestsMostFrequentCategoryPairAndItemCode()
	{
		// Arrange
		await ResetAsync();
		ReceiptEntity receipt = Receipt(new DateOnly(2026, 2, 1));

		await SeedAsync(
			[receipt],
			[
				Item(receipt.Id, "Whole Milk", category: "Groceries", subcategory: "Dairy", itemCode: "MILK-001"),
				Item(receipt.Id, "Whole Milk", category: "Groceries", subcategory: "Dairy", itemCode: "MILK-001"),
				Item(receipt.Id, "Whole Milk", category: "Household", subcategory: "Misc", itemCode: "MILK-999"),
			]);

		// Act
		ItemTemplateHistoryCandidate candidate = (await CreateService().GetHistoryCandidatesAsync(0, 50, 2, CancellationToken.None)).Data.Single();

		// Assert
		candidate.SuggestedCategory.Should().Be("Groceries");
		candidate.SuggestedSubcategory.Should().Be("Dairy");
		candidate.SuggestedItemCode.Should().Be("MILK-001");
	}

	[Fact]
	public async Task GetHistoryCandidatesAsync_SuggestsMostRecentUnitPrice()
	{
		// Arrange
		await ResetAsync();
		ReceiptEntity older = Receipt(new DateOnly(2026, 1, 5));
		ReceiptEntity newer = Receipt(new DateOnly(2026, 4, 5));

		await SeedAsync(
			[older, newer],
			[
				Item(older.Id, "Whole Milk", unitPrice: 2.49m),
				Item(older.Id, "Whole Milk", unitPrice: 2.49m),
				Item(newer.Id, "Whole Milk", unitPrice: 3.99m),
			]);

		// Act
		ItemTemplateHistoryCandidate candidate = (await CreateService().GetHistoryCandidatesAsync(0, 50, 2, CancellationToken.None)).Data.Single();

		// Assert — most recent price wins even though the older price is more frequent
		candidate.SuggestedUnitPrice.Should().Be(3.99m);
		candidate.LastPurchasedAt.Should().Be(new DateOnly(2026, 4, 5));
	}

	[Fact]
	public async Task GetHistoryCandidatesAsync_IgnoresNullItemCodesWhenSuggesting()
	{
		// Arrange
		await ResetAsync();
		ReceiptEntity receipt = Receipt(new DateOnly(2026, 2, 1));

		await SeedAsync(
			[receipt],
			[
				Item(receipt.Id, "Whole Milk", itemCode: null),
				Item(receipt.Id, "Whole Milk", itemCode: null),
				Item(receipt.Id, "Whole Milk", itemCode: "MILK-001"),
			]);

		// Act
		ItemTemplateHistoryCandidate candidate = (await CreateService().GetHistoryCandidatesAsync(0, 50, 2, CancellationToken.None)).Data.Single();

		// Assert — nulls are not a candidate code, so the only real code wins
		candidate.SuggestedItemCode.Should().Be("MILK-001");
	}

	[Fact]
	public async Task GetHistoryCandidatesAsync_ReturnsNullSuggestions_WhenNoItemCodeExists()
	{
		// Arrange
		await ResetAsync();
		ReceiptEntity receipt = Receipt(new DateOnly(2026, 2, 1));

		await SeedAsync(
			[receipt],
			[
				Item(receipt.Id, "Whole Milk", category: "", subcategory: null, itemCode: null),
				Item(receipt.Id, "Whole Milk", category: "", subcategory: null, itemCode: null),
			]);

		// Act
		ItemTemplateHistoryCandidate candidate = (await CreateService().GetHistoryCandidatesAsync(0, 50, 2, CancellationToken.None)).Data.Single();

		// Assert
		candidate.SuggestedItemCode.Should().BeNull();
		candidate.SuggestedCategory.Should().BeNull();
		candidate.SuggestedSubcategory.Should().BeNull();
	}

	[Fact]
	public async Task GetHistoryCandidatesAsync_OrdersByOccurrenceCountThenName()
	{
		// Arrange
		await ResetAsync();
		ReceiptEntity receipt = Receipt(new DateOnly(2026, 2, 1));

		await SeedAsync(
			[receipt],
			[
				Item(receipt.Id, "Bananas"),
				Item(receipt.Id, "Bananas"),
				Item(receipt.Id, "Bananas"),
				Item(receipt.Id, "Zucchini"),
				Item(receipt.Id, "Zucchini"),
				Item(receipt.Id, "Apples"),
				Item(receipt.Id, "Apples"),
			]);

		// Act
		PagedResult<ItemTemplateHistoryCandidate> result = await CreateService().GetHistoryCandidatesAsync(0, 50, 2, CancellationToken.None);

		// Assert — count desc first, then name asc within the same count
		result.Data.Select(c => c.Name).Should().ContainInOrder("Bananas", "Apples", "Zucchini");
	}

	[Fact]
	public async Task GetHistoryCandidatesAsync_PagesResultsWithStableTotal()
	{
		// Arrange
		await ResetAsync();
		ReceiptEntity receipt = Receipt(new DateOnly(2026, 2, 1));

		await SeedAsync(
			[receipt],
			[
				Item(receipt.Id, "Bananas"),
				Item(receipt.Id, "Bananas"),
				Item(receipt.Id, "Bananas"),
				Item(receipt.Id, "Zucchini"),
				Item(receipt.Id, "Zucchini"),
				Item(receipt.Id, "Apples"),
				Item(receipt.Id, "Apples"),
			]);

		// Act
		PagedResult<ItemTemplateHistoryCandidate> firstPage = await CreateService().GetHistoryCandidatesAsync(0, 2, 2, CancellationToken.None);
		PagedResult<ItemTemplateHistoryCandidate> secondPage = await CreateService().GetHistoryCandidatesAsync(2, 2, 2, CancellationToken.None);
		PagedResult<ItemTemplateHistoryCandidate> pastEnd = await CreateService().GetHistoryCandidatesAsync(50, 2, 2, CancellationToken.None);

		// Assert
		firstPage.Total.Should().Be(3);
		firstPage.Data.Select(c => c.Name).Should().Equal("Bananas", "Apples");
		secondPage.Total.Should().Be(3);
		secondPage.Data.Select(c => c.Name).Should().Equal("Zucchini");
		pastEnd.Total.Should().Be(3);
		pastEnd.Data.Should().BeEmpty();
	}

	[Fact]
	public async Task GetHistoryCandidatesAsync_ReturnsEmptyPage_WhenNoHistoryExists()
	{
		// Arrange
		await ResetAsync();

		// Act
		PagedResult<ItemTemplateHistoryCandidate> result = await CreateService().GetHistoryCandidatesAsync(0, 50, 2, CancellationToken.None);

		// Assert
		result.Total.Should().Be(0);
		result.Data.Should().BeEmpty();
		result.Offset.Should().Be(0);
		result.Limit.Should().Be(50);
	}

	private ItemTemplateHistoryCandidateService CreateService() => new(new FixtureDbContextFactory(fixture));

	private async Task ResetAsync()
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		await context.Database.ExecuteSqlRawAsync(
			"""TRUNCATE "ReceiptItems", "Receipts", "ItemTemplates", "DistinctDescriptions" RESTART IDENTITY CASCADE;""");
	}

	private async Task SeedAsync(
		IEnumerable<ReceiptEntity> receipts,
		IEnumerable<ReceiptItemEntity> items,
		IEnumerable<ItemTemplateEntity>? templates = null)
	{
		await using ApplicationDbContext context = fixture.CreateDbContext();
		context.Receipts.AddRange(receipts);
		context.ReceiptItems.AddRange(items);

		if (templates is not null)
		{
			context.ItemTemplates.AddRange(templates);
		}

		await context.SaveChangesAsync();
	}

	private static ReceiptEntity Receipt(DateOnly date) => new()
	{
		Id = Guid.NewGuid(),
		Location = "Test Location",
		Date = date,
		TaxAmount = 0m,
		TaxAmountCurrency = Currency.USD,
	};

	private static ReceiptItemEntity Item(
		Guid receiptId,
		string description,
		string category = "Groceries",
		string? subcategory = "Dairy",
		string? itemCode = "CODE-1",
		decimal unitPrice = 1.00m) => new()
		{
			Id = Guid.NewGuid(),
			ReceiptId = receiptId,
			Description = description,
			ReceiptItemCode = itemCode,
			Quantity = 1m,
			UnitPrice = unitPrice,
			UnitPriceCurrency = Currency.USD,
			TotalAmount = unitPrice,
			TotalAmountCurrency = Currency.USD,
			Category = category,
			Subcategory = subcategory,
		};

	private static ItemTemplateEntity Template(string name) => new()
	{
		Id = Guid.NewGuid(),
		Name = name,
	};

	private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
