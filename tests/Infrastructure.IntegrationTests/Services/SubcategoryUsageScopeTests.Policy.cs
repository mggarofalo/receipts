using System.Net;
using System.Text.Json;
using API.Services;
using Application.Models;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Repositories;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Moq;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public partial class SubcategoryUsageScopeTests
{
	[Theory]
	[InlineData("inactive")]
	[InlineData("trashed-item")]
	[InlineData("trashed-receipt")]
	public async Task MatchingInactiveAndTrashedUsage_StillProtectsDelete_WithHonestReceiptExamples(string state)
	{
		Seed seed = await SeedAsync();
		await using (ApplicationDbContext edit = fixture.CreateDbContext())
		{
			if (state == "inactive") { (await edit.Categories.SingleAsync(row => row.Id == seed.CategoryA)).IsActive = false; (await edit.Subcategories.SingleAsync(row => row.Id == seed.SubcategoryA)).IsActive = false; }
			else if (state == "trashed-item")
			{
				edit.ReceiptItems.Remove(await edit.ReceiptItems.SingleAsync(row => row.ReceiptId == seed.Receipt));
			}
			else
			{
				edit.Receipts.Remove(await edit.Receipts.SingleAsync(row => row.Id == seed.Receipt));
			}

			await edit.SaveChangesAsync();
		}
		Mock<IEntityChangeNotifier> notifier = new();
		await using var app = await CreateHostAsync(notifier.Object);
		using HttpClient client = app.GetTestClient();
		using var response = await client.DeleteAsync($"/api/subcategories/{seed.SubcategoryA}");
		response.StatusCode.Should().Be(HttpStatusCode.Conflict);
		using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		problem.RootElement.GetProperty("receiptItemCount").GetInt32().Should().Be(1);
		JsonElement examples = problem.RootElement.GetProperty("affectedReceipts");
		examples.GetArrayLength().Should().Be(1);
		examples[0].GetProperty("id").GetGuid().Should().Be(seed.Receipt);
		examples[0].GetProperty("isDeleted").GetBoolean().Should().Be(state == "trashed-receipt");
		notifier.Verify(instance => instance.NotifyDeleted("subcategory", seed.SubcategoryA), Times.Never);
	}

	[Fact]
	public async Task Usage_CountsOnlyExactParentAndChildPair_AndDeduplicatesMixedReceipts()
	{
		Seed seed = await SeedAsync();
		await using (ApplicationDbContext edit = fixture.CreateDbContext())
		{
			foreach (var pair in new (string, string?)[] { ("B", "Shared"), ("B", "Shared"), ("B", "Other"), ("b", "Shared"), ("B", "shared"), ("B", "Shared "), ("B", (string?)null), ("B", "") })
			{
				ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(seed.Receipt);
				item.Category = pair.Item1; item.Subcategory = pair.Item2;
				edit.ReceiptItems.Add(item);
			}
			await edit.SaveChangesAsync();
		}
		SubcategoryUsage usage = await new SubcategoryRepository(new Factory(fixture)).GetUsageAsync(seed.CategoryB, "Shared", 20, CancellationToken.None);
		usage.ReceiptItemCount.Should().Be(2);
		usage.AffectedReceipts.Should().ContainSingle().Which.ReceiptId.Should().Be(seed.Receipt);
	}

	[Fact]
	public async Task ReceiptSample_IsDistinctStableAndCapped_WhileCountIncludesAllMatchingItemsInTrash()
	{
		Seed seed = await SeedAsync();
		Guid Id(int number) => Guid.Parse($"00000000-0000-0000-0000-{number:000000000000}");
		await using (ApplicationDbContext add = fixture.CreateDbContext())
		{
			for (int number = 25; number >= 1; number--)
			{
				ReceiptEntity receipt = ReceiptEntityGenerator.Generate(); receipt.Id = Id(number); receipt.Date = new DateOnly(2024, 1, number <= 5 ? 2 : 1);
				add.Receipts.Add(receipt);
				for (int itemIndex = 0; itemIndex < 2; itemIndex++) { ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id); item.Category = "B"; item.Subcategory = "Shared"; add.ReceiptItems.Add(item); }
			}
			await add.SaveChangesAsync();
		}
		Guid third = Id(3), fourth = Id(4);
		await using (ApplicationDbContext trash = fixture.CreateDbContext())
		{
			trash.Receipts.Remove(await trash.Receipts.SingleAsync(row => row.Id == third));
			trash.ReceiptItems.Remove(await trash.ReceiptItems.FirstAsync(row => row.ReceiptId == fourth));
			await trash.SaveChangesAsync();
		}
		SubcategoryUsage usage = await new SubcategoryRepository(new Factory(fixture)).GetUsageAsync(seed.CategoryB, "Shared", 20, CancellationToken.None);
		usage.ReceiptItemCount.Should().Be(50);
		usage.AffectedReceipts.Select(row => row.ReceiptId).Should().Equal(Enumerable.Range(1, 20).Select(Id));
		usage.AffectedReceipts.Single(row => row.ReceiptId == Id(3)).IsDeleted.Should().BeTrue();
		usage.AffectedReceipts.Single(row => row.ReceiptId == Id(4)).IsDeleted.Should().BeFalse();
	}

	[Fact]
	public async Task DeletedParent_IsResolvedByItsId_AndMissingParentNeverFallsBackToGlobalUsage()
	{
		Seed seed = await SeedAsync();
		await using (ApplicationDbContext trash = fixture.CreateDbContext()) { trash.Categories.Remove(await trash.Categories.SingleAsync(row => row.Id == seed.CategoryA)); await trash.SaveChangesAsync(); }
		SubcategoryRepository repository = new(new Factory(fixture));
		(await repository.GetUsageAsync(seed.CategoryA, "Shared", 20, CancellationToken.None)).ReceiptItemCount.Should().Be(1);
		(await repository.GetUsageAsync(seed.CategoryB, "Shared", 20, CancellationToken.None)).ReceiptItemCount.Should().Be(0);
		Func<Task> missing = () => repository.GetUsageAsync(Guid.NewGuid(), "Shared", 20, CancellationToken.None);
		await missing.Should().ThrowAsync<InvalidOperationException>();
	}

	[Theory]
	[InlineData("parent")]
	[InlineData("child")]
	public async Task RenameAndRecreation_UseCurrentNames_WithoutRewritingHistoricalSnapshots(string renamed)
	{
		Seed seed = await SeedAsync();
		Guid recreatedParent = renamed == "parent" ? Guid.NewGuid() : seed.CategoryA;
		await using (ApplicationDbContext edit = fixture.CreateDbContext())
		{
			if (renamed == "parent")
			{
				(await edit.Categories.SingleAsync(row => row.Id == seed.CategoryA)).Name = "Renamed";
			}
			else
			{
				(await edit.Subcategories.SingleAsync(row => row.Id == seed.SubcategoryA)).Name = "Renamed";
			}

			await edit.SaveChangesAsync();
			if (renamed == "parent")
			{
				edit.Categories.Add(new() { Id = recreatedParent, Name = "A", IsActive = true });
			}

			edit.Subcategories.Add(new() { Id = Guid.NewGuid(), CategoryId = recreatedParent, Name = "Shared", IsActive = true });
			await edit.SaveChangesAsync();
		}
		SubcategoryRepository repository = new(new Factory(fixture));
		(await repository.GetUsageAsync(seed.CategoryA, renamed == "child" ? "Renamed" : "Shared", 20, CancellationToken.None)).ReceiptItemCount.Should().Be(0);
		(await repository.GetUsageAsync(recreatedParent, "Shared", 20, CancellationToken.None)).ReceiptItemCount.Should().Be(1);
		await using ApplicationDbContext verify = fixture.CreateDbContext();
		ReceiptItemEntity historical = await verify.ReceiptItems.SingleAsync(row => row.ReceiptId == seed.Receipt);
		historical.Category.Should().Be("A"); historical.Subcategory.Should().Be("Shared");
	}

	[Fact]
	public async Task CategoryGuard_ProtectsUnknownHistoricalChildLabels_AndMissingDeleteStays404()
	{
		Seed seed = await SeedAsync();
		await using (ApplicationDbContext edit = fixture.CreateDbContext()) { (await edit.ReceiptItems.SingleAsync(row => row.ReceiptId == seed.Receipt)).Subcategory = "No current suggestion"; await edit.SaveChangesAsync(); }
		Mock<IEntityChangeNotifier> notifier = new();
		await using var app = await CreateHostAsync(notifier.Object);
		using HttpClient client = app.GetTestClient();
		(await client.DeleteAsync($"/api/categories/{seed.CategoryA}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
		(await client.DeleteAsync($"/api/subcategories/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
		notifier.Verify(instance => instance.NotifyDeleted(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
	}
}
