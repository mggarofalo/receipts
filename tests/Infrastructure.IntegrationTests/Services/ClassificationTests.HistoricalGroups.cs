using System.Net;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Infrastructure.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task HistoricalGroupsKeepNullAndEmptyCodesDistinctAndCountPastLastPage()
	{
		var seed = await HistoricalSeedAsync();
		await using (var db = fixture.CreateDbContext())
		{
			ReceiptItemEntity original = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
			original.ReceiptItemCode = null;
			await new ReceiptItemRepository(new ActorFactory(fixture)).UpdateAsync([original], default);
		}
		HistoricalGroupKey source = (await HistoricalRequest(seed.Item, seed.Revision, seed.Root)).Groups.Single().Group;
		Guid receiptId;
		await using (var db = fixture.CreateDbContext())
		{
			receiptId = (await db.ReceiptItems.SingleAsync(x => x.Id == seed.Item)).ReceiptId;
		}
		ReceiptItemEntity emptyCode = ReceiptItemEntityGenerator.Generate(receiptId);
		emptyCode.Description = source.RawDescription;
		emptyCode.ReceiptItemCode = "";
		await new ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync([emptyCode], default);

		var browse = new HistoricalGroupReader(new Factory(fixture));
		HistoricalGroupPage page = await browse.BrowseAsync(new(Search: source.RawDescription));
		page.Items.Should().ContainSingle(x => x.Group.ItemCode == null && x.PurchaseCount == 1);
		page.Items.Should().ContainSingle(x => x.Group.ItemCode == "" && x.PurchaseCount == 1);
		HistoricalGroupPage pastEnd = await browse.BrowseAsync(new(1000, 25, source.RawDescription));
		pastEnd.Items.Should().BeEmpty();
		pastEnd.TotalGroups.Should().Be(page.TotalGroups);
		pastEnd.NextOffset.Should().BeNull();
	}

	[Fact]
	public async Task HistoricalGroupsUseExactSourceAcrossReceiptsAndPageInFrequencyOrder()
	{
		var seed = await HistoricalSeedAsync();
		HistoricalGroupKey source = (await HistoricalRequest(seed.Item, seed.Revision, seed.Root)).Groups.Single().Group;
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		receipt.Location = source.Store;
		await using (var db = fixture.CreateDbContext())
		{
			db.Receipts.Add(receipt);
			await db.SaveChangesAsync();
		}
		ReceiptItemEntity duplicate = ReceiptItemEntityGenerator.Generate(receipt.Id);
		duplicate.Description = source.RawDescription;
		duplicate.ReceiptItemCode = source.ItemCode;
		ReceiptItemEntity differentCode = ReceiptItemEntityGenerator.Generate(receipt.Id);
		differentCode.Description = source.RawDescription;
		differentCode.ReceiptItemCode = source.ItemCode is null ? "" : source.ItemCode + "-different";
		await new ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync([duplicate, differentCode], default);
		ReceiptEntity otherStore = ReceiptEntityGenerator.Generate();
		otherStore.Location = source.Store + " other";
		await using (var db = fixture.CreateDbContext())
		{
			db.Receipts.Add(otherStore);
			await db.SaveChangesAsync();
		}
		ReceiptItemEntity otherStoreItem = ReceiptItemEntityGenerator.Generate(otherStore.Id);
		otherStoreItem.Description = source.RawDescription;
		otherStoreItem.ReceiptItemCode = source.ItemCode;
		await new ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync([otherStoreItem], default);
		var browse = new HistoricalGroupReader(new Factory(fixture));
		HistoricalGroupPage matching = await browse.BrowseAsync(new(Search: source.RawDescription));
		matching.Items[0].Group.Should().Be(source);
		matching.Items[0].PurchaseCount.Should().Be(2);
		matching.Items.Should().Contain(x => x.Group.ItemCode == differentCode.ReceiptItemCode && x.PurchaseCount == 1);
		matching.Items.Should().Contain(x => x.Group.Store == otherStore.Location && x.PurchaseCount == 1);
		HistoricalGroupPage first = await browse.BrowseAsync(new(0, 1, source.RawDescription));
		first.NextOffset.Should().Be(1);
		(await browse.BrowseAsync(new(1, 1, source.RawDescription))).Items.Should().ContainSingle();
		await using (var db = fixture.CreateDbContext())
		{
			await db.ReceiptItems.Where(x => x.Id == duplicate.Id)
				.ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow));
		}
		(await browse.BrowseAsync(new(Search: source.RawDescription))).Items.Single(x => x.Group == source)
			.PurchaseCount.Should().Be(1);
		await new ReceiptRepository(new ActorFactory(fixture)).DeleteAsync([otherStore.Id], default);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.ReceiptItems.IgnoreQueryFilters().SingleAsync(x => x.Id == otherStoreItem.Id))
					.DeletedAt.Should().NotBeNull();
		}
		(await browse.BrowseAsync(new(Search: source.RawDescription))).Items
			.Should().NotContain(x => x.Group.Store == otherStore.Location);
	}

	[Fact]
	public async Task HistoricalGroupsExposeCurrentCategoryConflictAndHttpBounds()
	{
		var seed = await HistoricalSeedAsync();
		HistoricalGroupKey source = (await HistoricalRequest(seed.Item, seed.Revision, seed.Root)).Groups.Single().Group;
		await using (var db = fixture.CreateDbContext())
		{
			ReceiptItemEntity second = ReceiptItemEntityGenerator.Generate(
				(await db.ReceiptItems.SingleAsync(x => x.Id == seed.Item)).ReceiptId);
			second.Description = source.RawDescription;
			second.ReceiptItemCode = source.ItemCode;
			await new ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync([second], default);
			var initial = (await Service().HistoryAsync(second.Id, null, 1)).Items.Single();
			await Service().AssignAsync(Actor, second.Id,
				new(seed.Child, seed.Revision, initial.Id, Guid.NewGuid(), "Different path"));
			await Service().AssignAsync(Actor, seed.Item,
				new((Guid?)null, seed.Revision, seed.Assignment, Guid.NewGuid(), "Explicitly unresolved"));
		}
		var browse = new HistoricalGroupReader(new Factory(fixture));
		HistoricalGroupSummary conflict = (await browse.BrowseAsync(new(Search: source.RawDescription, ConflictsOnly: true)))
			.Items.Single(x => x.Group == source);
		conflict.Conflict.Should().BeTrue();
		conflict.Unresolved.Should().BeTrue();
		conflict.Unassigned.Should().BeFalse();
		conflict.CurrentCategories.Sum(x => x.PurchaseCount).Should().Be(conflict.PurchaseCount);
		conflict.CurrentCategories.Should().Contain(x => x.CategoryId == seed.Child && x.Path!.Contains(" / Child"));
		(await browse.BrowseAsync(new(Search: source.RawDescription, UnresolvedOnly: true)))
			.Items.Should().Contain(x => x.Group == source);
		(await browse.BrowseAsync(new(Search: source.RawDescription, UnassignedOnly: true)))
			.Items.Should().NotContain(x => x.Group == source);
		await using var app = await HistoricalHttpHostAsync();
		using var client = app.GetTestClient();
		const string route = "/api/historical-reclassifications/groups";
		(await client.GetAsync(route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		client.DefaultRequestHeaders.Add("X-Test-Actor", Actor);
		await HistoricalJson(await client.GetAsync(route + "?limit=26"), HttpStatusCode.BadRequest);
		await HistoricalJson(await client.GetAsync(route + "?offset=-1"), HttpStatusCode.BadRequest);
		var response = await HistoricalJson(await client.GetAsync(route + "?limit=1&conflictsOnly=true"));
		response.GetProperty("items").GetArrayLength().Should().Be(1);
	}
}
