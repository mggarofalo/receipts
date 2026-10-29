using System.Net;
using System.Text.Json;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task HostedMappingWithoutJevSkipsProtectedPrefixAndSupportsPublicUndoAfterManualChange()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).HostedMappingHttpCore();
	}

	private async Task HostedMappingHttpCore()
	{
		var captured = await CaptureExecutableAsync();
		await using var db = fixture.CreateDbContext();
		var original = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == captured.Item);
		var purchases = Enumerable.Range(1, 102).Select(n =>
		{
			var item = ReceiptItemEntityGenerator.Generate(original.ReceiptId);
			item.Id = Guid.ParseExact((n <= 100 ? n.ToString("x32") : "ffffffffffffffffffffffffffffff" + n.ToString("x2")), "N");
			item.Description = original.Description;
			item.ReceiptItemCode = original.ReceiptItemCode;
			item.Category = ""; item.Subcategory = null;
			return item;
		}).ToList();
		await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync(purchases, default);
		db.CategoryAssistanceReviewPreferences.Add(new CategoryAssistanceReviewPreferenceEntity
		{
			Actor = "another-reviewer",
			AttemptId = Guid.NewGuid(),
			Revision = Guid.NewGuid(),
			Location = captured.Request.Group.Store,
			Description = captured.Request.Group.RawDescription,
			ItemCodeKey = captured.Request.Group.ItemCode ?? "",
			State = "active",
			ExcludedItemIdsJson = JsonSerializer.Serialize(purchases.Take(100).Select(x => x.Id))
		});
		await db.SaveChangesAsync();
		await db.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"Settings\" SET \"Enabled\" = false");

		// This host has no Jev provider registration or credentials. StartAsync alone
		// starts processing: the acceptance path never calls RunOnceAsync itself.
		await using var app = await HistoricalHttpHostAsync(backgroundMappings: true);
		using var client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("X-Test-Actor", Actor);
		const string route = "/api/historical-reclassifications";
		JsonElement activity = default;
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		do
		{
			activity = await HistoricalJson(await client.GetAsync(route + "/activity", deadline.Token));
			if (activity.GetProperty("items").EnumerateArray().Any(x => x.GetProperty("progress").GetProperty("applied").GetInt64() == 3)) { break; }
			await Task.Delay(100, deadline.Token);
		} while (true);
		var entry = activity.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("progress").GetProperty("applied").GetInt64() == 3);
		Guid operation = entry.GetProperty("operationId").GetGuid();
		(await db.Classifications.CountAsync(x => x.Source == "approved_mapping")).Should().Be(3,
			"100 protected low-ID purchases must not hide later eligible purchases");
		var automatic = (await Service().HistoryAsync(captured.Item, null, 1)).Items.Single();
		var manual = await Service().AssignAsync(Actor, captured.Item,
			new(null, captured.Request.TaxonomyRevisionId, automatic.Id, Guid.NewGuid(), "Keep unclassified"));
		var undone = await HistoricalJson(await client.PostAsync($"{route}/{operation}/undo", HistoricalBody(new
		{
			taxonomyRevisionId = captured.Request.TaxonomyRevisionId,
			reason = "Undo remembered choice",
			limit = 100
		})));
		undone.GetProperty("state").GetString().Should().Be("undone");
		undone.GetProperty("undone").GetInt64().Should().Be(2);
		undone.GetProperty("undoConflicted").GetInt64().Should().Be(1);
		(await Service().HistoryAsync(captured.Item, null, 1)).Items.Single().Id.Should().Be(manual.Id);
		foreach (var item in purchases.Skip(100))
		{
			var current = (await Service().HistoryAsync(item.Id, null, 1)).Items.Single();
			current.CategoryId.Should().BeNull(); current.Source.Should().Be("manual");
		}
		client.DefaultRequestHeaders.Remove("X-Test-Actor");
		client.DefaultRequestHeaders.Add("X-Test-Actor", "stranger");
		(await HistoricalJson(await client.GetAsync(route + "/activity"))).GetProperty("items").GetArrayLength().Should().Be(0);
		await HistoricalJson(await client.PostAsync($"{route}/{operation}/undo", HistoricalBody(new
		{
			taxonomyRevisionId = captured.Request.TaxonomyRevisionId,
			reason = "Undo remembered choice",
			limit = 100
		})), HttpStatusCode.NotFound);
		await app.StopAsync();
		// A fresh worker must keep both the manual choice and undo suppression.
		using var restarted = MappingWorker();
		await restarted.RunOnceAsync();
		(await db.Classifications.CountAsync(x => x.Source == "approved_mapping")).Should().Be(3);
	}
}
