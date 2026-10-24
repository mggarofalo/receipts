using Application.Models;
using Application.Models.Search;
using Common;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class PurchaseSearchReaderTests(PostgresFixture fixture)
{
	[Theory]
	[InlineData("date")]
	[InlineData("description")]
	[InlineData("location")]
	[InlineData("unitPrice")]
	[InlineData("quantity")]
	[InlineData("totalAmount")]
	public async Task SearchAsync_SortsTheWholeResultBeforePagingInBothDirections(string column)
	{
		await ResetAsync();
		// The largest sort values have the smallest IDs; sorting one downloaded
		// page or using the default date order cannot satisfy both directions.
		await using (var db = fixture.CreateDbContext())
		{
			for (int id = 1; id <= 3; id++)
			{
				int rank = 4 - id;
				var receipt = Receipt(id, new DateOnly(2025, 1, column == "date" ? rank : 1));
				receipt.Location = column == "location" ? $"Store {rank}" : "Store";
				var item = Item(id, receipt.Id, column == "description" ? $"Milk {rank}" : "Milk");
				item.UnitPrice = column == "unitPrice" ? rank : 1;
				item.Quantity = column == "quantity" ? rank : 1;
				item.TotalAmount = column == "totalAmount" ? rank * rank : 1;
				db.Receipts.Add(receipt); db.ReceiptItems.Add(item);
			}
			await db.SaveChangesAsync();
		}
		foreach (string direction in new[] { "asc", "desc" })
		{
			var first = await Reader().SearchAsync("Milk", 0, 1, new(column, direction), CancellationToken.None);
			var next = await Reader().SearchAsync("Milk", 1, 2, new(column, direction), CancellationToken.None);
			first.Total.Should().Be(3); next.Total.Should().Be(3);
			first.Items.Concat(next.Items).Select(x => x.Id).Should().Equal(direction == "asc"
				? new[] { Id(3), Id(2), Id(1) } : new[] { Id(1), Id(2), Id(3) });
		}
	}

	[Fact]
	public async Task SearchAsync_AppliesEveryInclusiveBoundaryBeforeCountAndPage()
	{
		await ResetAsync();
		await using (var db = fixture.CreateDbContext())
		{
			for (int id = 1; id <= 8; id++)
			{
				var receipt = Receipt(id, new DateOnly(2025, 1, id == 3 ? 9 : id == 4 ? 12 : id == 2 ? 11 : 10));
				receipt.Location = id == 7 ? "50 percent Market" : "50% MARKET";
				var item = Item(id, receipt.Id, id == 8 ? "Bread" : "Milk");
				item.UnitPrice = id == 1 ? 1.2345m : id == 2 ? 2.3456m : id == 5 ? 1.2344m : id == 6 ? 2.3457m : 2m;
				db.Receipts.Add(receipt); db.ReceiptItems.Add(item);
			}
			await db.SaveChangesAsync();
		}
		var options = new PurchaseSearchOptions("unitPrice", "asc", "50% market", new(2025, 1, 10), new(2025, 1, 11), 1.2345m, 2.3456m);
		var first = await Reader().SearchAsync("milk", 0, 1, options, CancellationToken.None);
		var second = await Reader().SearchAsync("milk", 1, 1, options, CancellationToken.None);
		first.Total.Should().Be(2); second.Total.Should().Be(2);
		first.Items.Should().ContainSingle().Which.Id.Should().Be(Id(1));
		second.Items.Should().ContainSingle().Which.Id.Should().Be(Id(2));
	}

	[Theory]
	[InlineData("asc")]
	[InlineData("desc")]
	public async Task SearchAsync_UsesStableReceiptAndItemTieBreakersForPriceSort(string direction)
	{
		await ResetAsync();
		await SeedAsync(Receipt(2), Item(1, Id(2), "Milk"));
		await SeedAsync(Receipt(1), Item(3, Id(1), "Milk"), Item(2, Id(1), "Milk"));
		var options = new PurchaseSearchOptions("unitPrice", direction);
		var first = await Reader().SearchAsync("Milk", 0, 1, options, CancellationToken.None);
		var second = await Reader().SearchAsync("Milk", 1, 2, options, CancellationToken.None);
		first.Items.Concat(second.Items).Select(x => x.Id).Should().Equal(Id(2), Id(3), Id(1));
	}
	[Fact]
	public async Task SearchAsync_FindsOldPurchasesBeyondTwoHundredAndCountsAllPages()
	{
		await ResetAsync();
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			for (int index = 0; index < 205; index++)
			{
				ReceiptEntity receipt = Receipt(index + 1, new DateOnly(2024, 1, 1).AddDays(index));
				db.Receipts.Add(receipt);
				db.ReceiptItems.Add(Item(index + 1, receipt.Id, "Whole Milk"));
			}
			await db.SaveChangesAsync();
		}
		PurchaseSearchResult result = await Reader().SearchAsync("wHoLe MiLk", 200, 25, new PurchaseSearchOptions(), CancellationToken.None);
		result.Total.Should().Be(205);
		result.Offset.Should().Be(200);
		result.Limit.Should().Be(25);
		result.Items.Select(x => x.Id).Should().Equal(Id(5), Id(4), Id(3), Id(2), Id(1));
		(await Reader().SearchAsync("Whole Milk", 205, 25, new PurchaseSearchOptions(), CancellationToken.None)).Items.Should().BeEmpty();
	}

	[Theory]
	[InlineData("50%", "50 percent")]
	[InlineData("a_b", "axb")]
	[InlineData("a\\b", "ab")]
	public async Task SearchAsync_TreatsWildcardAndEscapeCharactersAsLiteral(string phrase, string decoy)
	{
		await ResetAsync();
		await SeedAsync(Receipt(1), Item(1, Id(1), "prefix " + phrase + " suffix"), Item(2, Id(1), decoy));
		PurchaseSearchResult result = await Reader().SearchAsync(phrase, 0, 25, new PurchaseSearchOptions(), CancellationToken.None);
		result.Total.Should().Be(1);
		result.Items.Should().ContainSingle().Which.Id.Should().Be(Id(1));
	}

	[Fact]
	public async Task SearchAsync_SearchesRawFactsWithoutTrustingUnreviewedCanonicalLinks()
	{
		await ResetAsync();
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			ReceiptEntity ordinary = Receipt(1);
			ReceiptEntity store = Receipt(2); store.Location = "Needle Market";
			db.Receipts.AddRange(ordinary, store);
			db.NormalizedDescriptions.AddRange(
				Canonical(10, "Needle canonical", NormalizedDescriptionStatus.Active),
				Canonical(11, "Needle pending", NormalizedDescriptionStatus.PendingReview),
				Canonical(12, "Needle rejected", NormalizedDescriptionStatus.Rejected));
			ReceiptItemEntity raw = Item(1, ordinary.Id, "Needle raw");
			ReceiptItemEntity code = Item(2, ordinary.Id); code.ReceiptItemCode = "SKU-NEEDLE";
			ReceiptItemEntity active = Item(3, ordinary.Id); active.NormalizedDescriptionId = Id(10);
			ReceiptItemEntity pending = Item(4, ordinary.Id); pending.NormalizedDescriptionId = Id(11);
			ReceiptItemEntity rejected = Item(5, ordinary.Id); rejected.NormalizedDescriptionId = Id(12);
			db.ReceiptItems.AddRange(raw, code, active, pending, rejected, Item(6, store.Id));
			await db.SaveChangesAsync();
		}
		PurchaseSearchResult result = await Reader().SearchAsync("needle", 0, 25, new PurchaseSearchOptions(), CancellationToken.None);
		result.Total.Should().Be(3);
		result.Items.Select(x => x.Id).Should().BeEquivalentTo([Id(1), Id(2), Id(6)]);
		result.Items.Single(x => x.Id == Id(1)).NormalizedDescriptionId.Should().BeNull();
	}

	[Fact]
	public async Task SearchAsync_IncludeUnreviewedFindsBothCanonicalNameAndVisibleLabel()
	{
		await ResetAsync();
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			NormalizedDescriptionEntity canonical = Canonical(10, "Canonical milk", NormalizedDescriptionStatus.Active);
			canonical.DisplayLabel = "Breakfast drink";
			db.NormalizedDescriptions.Add(canonical);
			db.Receipts.Add(Receipt(1));
			ReceiptItemEntity item = Item(1, Id(1)); item.NormalizedDescriptionId = canonical.Id;
			db.ReceiptItems.Add(item);
			await db.SaveChangesAsync();
		}
		foreach (string phrase in new[] { "canonical milk", "BREAKFAST DRINK" })
		{
			PurchaseSearchResult result = await Reader().SearchAsync(phrase, 0, 25, new PurchaseSearchOptions(IncludeUnreviewed: true), CancellationToken.None);
			result.Total.Should().Be(1);
			result.Items.Should().ContainSingle().Which.NormalizedDescription.Should().Be("Breakfast drink");
		}
	}

	[Fact]
	public async Task SearchAsync_ExcludesDeletedChildrenAndDeletedParents()
	{
		await ResetAsync();
		ReceiptEntity live = Receipt(1);
		ReceiptEntity deleted = Receipt(2);
		ReceiptItemEntity deletedItem = Item(2, live.Id, "Milk"); deletedItem.DeletedAt = DateTimeOffset.UtcNow;
		await SeedAsync(live, Item(1, live.Id, "Milk"), deletedItem);
		await SeedAsync(deleted, Item(3, deleted.Id, "Milk"));
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			await db.Receipts.Where(x => x.Id == deleted.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow));
		}
		PurchaseSearchResult result = await Reader().SearchAsync("Milk", 0, 25, new PurchaseSearchOptions(), CancellationToken.None);
		result.Total.Should().Be(1);
		result.Items.Should().ContainSingle().Which.Id.Should().Be(Id(1));
	}

	[Fact]
	public async Task SearchAsync_PreservesHistoricalPricesAndOrdersTiesAcrossPages()
	{
		await ResetAsync();
		ReceiptEntity receipt = Receipt(1);
		ReceiptItemEntity precise = Item(1, receipt.Id, "Milk");
		precise.Quantity = 1.2345m; precise.UnitPrice = 2.3456m; precise.TotalAmount = 2.90m;
		precise.UnitPriceCurrency = Currency.USD; precise.TotalAmountCurrency = Currency.USD;
		await SeedAsync(receipt, Item(2, receipt.Id, "Milk"), precise);
		await SeedAsync(Receipt(2), Item(3, Id(2), "Milk"));
		PurchaseSearchResult first = await Reader().SearchAsync("Milk", 0, 1, new PurchaseSearchOptions(), CancellationToken.None);
		first.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new PurchaseSearchItem(
			Id(1), receipt.Id, receipt.Date, receipt.Location, "Milk", null, null, null,
			1.2345m, 2.3456m, "USD", 2.90m, "USD"));
		PurchaseSearchResult next = await Reader().SearchAsync("Milk", 1, 2, new PurchaseSearchOptions(), CancellationToken.None);
		next.Items.Select(x => x.Id).Should().Equal(Id(2), Id(3));
		next.Total.Should().Be(3);
	}

	[Fact]
	public async Task SearchAsync_PropagatesCancellation()
	{
		using CancellationTokenSource cancellation = new(); cancellation.Cancel();
		Func<Task> search = () => Reader().SearchAsync("Milk", 0, 25, new PurchaseSearchOptions(), cancellation.Token);
		await search.Should().ThrowAsync<OperationCanceledException>();
	}

	[Theory]
	[InlineData("50%", "50 percent")]
	[InlineData("a_b", "axb")]
	[InlineData("a\\b", "ab")]
	public async Task ReceiptItemSearch_TreatsPatternCharactersLiterallyAcrossAllSearchableFields(string phrase, string decoy)
	{
		await ResetAsync();
		ReceiptItemEntity description = Item(1, Id(1), phrase);
		ReceiptItemEntity code = Item(2, Id(1)); code.ReceiptItemCode = phrase;
		ReceiptItemEntity category = Item(3, Id(1)); category.Category = phrase;
		ReceiptItemEntity subcategory = Item(4, Id(1)); subcategory.Subcategory = phrase;
		await SeedAsync(Receipt(1), description, code, category, subcategory, Item(5, Id(1), decoy));
		ReceiptItemRepository repository = new(new ContextFactory(fixture));
		var rows = await repository.GetAllAsync(0, 25, SortParams.Default, phrase, CancellationToken.None);
		rows.Select(x => x.Id).Should().BeEquivalentTo([Id(1), Id(2), Id(3), Id(4)]);
		(await repository.GetCountAsync(phrase, CancellationToken.None)).Should().Be(4);
	}

	private PurchaseSearchReader Reader() => new(new ContextFactory(fixture));
	private static Guid Id(int number) => Guid.Parse($"00000000-0000-0000-0000-{number:D12}");
	private static ReceiptEntity Receipt(int number, DateOnly? date = null) => new()
	{
		Id = Id(number),
		Date = date ?? new DateOnly(2025, 1, 1),
		Location = "Ordinary store",
		TaxAmountCurrency = Currency.USD,
	};
	private static ReceiptItemEntity Item(int number, Guid receiptId, string description = "Ordinary item") => new()
	{
		Id = Id(number),
		ReceiptId = receiptId,
		Description = description,
		Quantity = 1,
		UnitPrice = 3,
		UnitPriceCurrency = Currency.USD,
		TotalAmount = 3,
		TotalAmountCurrency = Currency.USD,
		Category = "Food",
	};
	private static NormalizedDescriptionEntity Canonical(int number, string name, NormalizedDescriptionStatus status) => new()
	{
		Id = Id(number),
		CanonicalName = name,
		Status = status,
		CreatedAt = DateTimeOffset.UtcNow,
	};
	private async Task SeedAsync(ReceiptEntity receipt, params ReceiptItemEntity[] items)
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		db.Receipts.Add(receipt); db.ReceiptItems.AddRange(items); await db.SaveChangesAsync();
	}
	private async Task ResetAsync()
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		await db.Database.ExecuteSqlRawAsync("""TRUNCATE "ReceiptItems", "Receipts", "NormalizedDescriptions", "DistinctDescriptions" RESTART IDENTITY CASCADE;""");
	}
	private sealed class ContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
	}
}
