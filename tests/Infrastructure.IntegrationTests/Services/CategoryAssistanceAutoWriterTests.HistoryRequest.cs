using Application.Classification;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class CategoryAssistanceAutoWriterTests
{
	[Fact]
	public async Task HistoryRequestStrongMatchFillsOnlyUncategorizedPurchasesButReviewStillOffersTheRest()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95, items: 3);
		Guid open = seed.Items[0], manual = seed.Items[1], cleared = seed.Items[2];
		foreach (var (item, category) in new[] { (manual, (Guid?)seed.OtherRoot), (cleared, null) })
		{
			var legacy = await CurrentAsync(item);
			await Classification().AssignAsync(seed.Actor, item, new(category, seed.Taxonomy, legacy.Id, Guid.NewGuid(), "Owner's choice"));
		}
		await ScopeToHistoryRequestAsync(seed);
		var manualBefore = await CurrentAsync(manual);
		var clearedBefore = await CurrentAsync(cleared);
		manualBefore.Should().Match<PurchaseClassificationEntity>(x => x.Source == "manual" && x.CategoryId == seed.OtherRoot);
		clearedBefore.Should().Match<PurchaseClassificationEntity>(x => x.Source == "manual" && x.CategoryId == null);

		// A person reviewing the explicit request may still choose to replace an existing category.
		var review = await Decisions().ListEligibleItemsAsync(seed.Actor, seed.Attempt, 0, 10, default);
		review.Items.Select(x => x.Id).Should().BeEquivalentTo(new[] { open, manual, cleared });

		(await Writer().ApplyNextAsync()).Should().BeTrue();

		(await CurrentAsync(open)).Should().Match<PurchaseClassificationEntity>(x => x.Source == "jev" && x.CategoryId == seed.Root);
		(await CurrentAsync(manual)).Should().BeEquivalentTo(manualBefore, "an automatic write never overwrites a manual category");
		(await CurrentAsync(cleared)).Should().BeEquivalentTo(clearedBefore, "an automatic write never overrides a deliberate clear");
		await using var db = fixture.CreateDbContext();
		(await db.HistoricalOperationItems.Select(x => x.ItemId).ToListAsync()).Should().Equal(open);
	}

	/// <summary>
	/// Turns the seeded attempt into the result of an explicit history request: the attempt carries the request scope and
	/// every purchase has a fresh explicit-request marker capturing its current assignment, as the historical store builds them.
	/// </summary>
	private async Task ScopeToHistoryRequestAsync(Seed seed)
	{
		Guid scope = Guid.NewGuid();
		await using (var db = fixture.CreateDbContext())
		{
			await db.CategoryAssistanceObservations.Where(x => x.AttemptId == seed.Attempt).ExecuteDeleteAsync();
			await db.CategoryAssistanceAttempts.Where(x => x.Id == seed.Attempt)
				.ExecuteUpdateAsync(s => s.SetProperty(x => x.RequestScope, scope));
		}
		foreach (Guid item in seed.Items) { await ObserveAsync(seed, item, seed.Actor); }
		await using (var db = fixture.CreateDbContext())
		{
			var markers = await db.CategoryAssistanceObservations.AsNoTracking().Where(x => x.AttemptId == seed.Attempt).ToListAsync();
			db.CategoryAssistanceObservations.RemoveRange(markers);
			await db.SaveChangesAsync();
			foreach (var marker in markers)
			{
				marker.SaveId = scope;
				marker.Evidence = "explicit_history_request";
			}
			db.CategoryAssistanceObservations.AddRange(markers);
			await db.SaveChangesAsync();
		}
	}

	private CategoryAssistanceDecisionStore Decisions()
	{
		var capability = new Mock<ICategoryAssistanceCapabilitySource>();
		capability.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CategoryAssistanceCapability(
			"jev", CategoryAssistanceCapabilityState.Ready, [CategorySuggestionPolicy.Model]));
		return new(new Factory(fixture), capability.Object, Options.Create(new ClassificationOptions { WritesEnabled = true }),
			TimeProvider.System);
	}
}
