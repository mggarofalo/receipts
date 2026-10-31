using System.Text.Json;
using Application.Classification;
using FluentAssertions;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class CategoryAssistanceAutoWriterTests
{
	private static readonly CategoryAssistanceCapability Ready =
		new("jev", CategoryAssistanceCapabilityState.Ready, [CategorySuggestionPolicy.Model]);

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task AfterASettingsSaveAnAppliedSourceSavedAgainIsAskedAgain(bool autoApply)
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95);
		(await Writer().ApplyNextAsync()).Should().BeTrue();
		var saved = await SaveSettingsAsync(seed, s => s with { AutoApply = false });
		if (autoApply) { saved = await SaveSettingsAsync(seed, s => s with { AutoApply = true }); }
		Guid later = await AddItemAsync(seed, attach: false);
		var dispatch = Dispatch();

		var work = await dispatch.ClaimQueuedAsync(default);

		work.Should().NotBeNull("the old result is not current under the new settings revision");
		work!.ItemId.Should().Be(later);
		work.AttemptId.Should().NotBe(seed.Attempt);
		await using (var db = fixture.CreateDbContext())
		{
			var attempt = await db.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == work.AttemptId);
			attempt.State.Should().Be("queued");
			attempt.SettingsRevision.Should().Be(saved.Revision);
			(await db.CategoryAssistanceObservations.AsNoTracking().SingleAsync(x => x.ReceiptItemId == later)).AttemptId.Should().Be(work.AttemptId);
			(await db.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.Attempt)).State.Should().Be("applied");
		}

		var claim = await dispatch.TryStartDispatchAsync(work, Ready, DateTimeOffset.UtcNow, default);
		claim.Should().NotBeNull();
		await dispatch.CompleteAsync(claim!, await SeededCompletionAsync(seed), Ready, default);
		(await Writer().ApplyNextAsync()).Should().BeTrue();

		(await CurrentAsync(seed.Items[0])).Source.Should().Be("jev", "the earlier write is kept");
		var current = await CurrentAsync(later);
		await using var verify = fixture.CreateDbContext();
		var asked = await verify.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == work.AttemptId);
		if (autoApply)
		{
			current.Should().Match<PurchaseClassificationEntity>(x => x.Source == "jev" && x.CategoryId == seed.Root);
			JsonSerializer.Deserialize<JevAutoWriteEvidence>(current.Reason)!.AttemptId.Should().Be(work.AttemptId);
			current.Reason.Should().Contain("\"Level\":\"Root\"", "the level is persisted by name");
			asked.State.Should().Be("applied");
		}
		else
		{
			current.Source.Should().Be("legacy_import");
			asked.State.Should().Be("suggested");
			(await new CategoryAssistanceReadStore(new Factory(fixture), TimeProvider.System).GetSuggestionAsync(work.AttemptId, default))
				.Should().NotBeNull();
		}
	}

	[Fact]
	public async Task WithoutASettingsSaveAnAppliedSourceSavedAgainAttachesToItsAttempt()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95);
		(await Writer().ApplyNextAsync()).Should().BeTrue();
		Guid later = await AddItemAsync(seed, attach: false);

		(await Dispatch().ClaimQueuedAsync(default)).Should().BeNull("an applied result under the same settings is not asked again");

		await using (var db = fixture.CreateDbContext())
		{
			(await db.CategoryAssistanceObservations.AsNoTracking().SingleAsync(x => x.ReceiptItemId == later)).AttemptId.Should().Be(seed.Attempt);
			(await db.CategoryAssistanceAttempts.CountAsync()).Should().Be(1);
		}
		(await Writer().ApplyNextAsync()).Should().BeTrue();
		(await CurrentAsync(later)).Should().Match<PurchaseClassificationEntity>(x => x.Source == "jev" && x.CategoryId == seed.Root);
	}

	[Fact]
	public async Task SettingsSaveRetiresUndecidedAttemptsAndLeavesOthersUntouched()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		var retired = new List<Guid> { seed.Attempt };
		retired.Add(await CloneAttemptAsync(seed, "queued", x =>
		{
			x.ClaimToken = Guid.NewGuid(); x.LeaseExpiresAt = now.AddMinutes(1); x.NextEligibleAt = now.AddHours(1); x.CompletedAt = null;
		}));
		retired.Add(await CloneAttemptAsync(seed, "abstained"));
		var kept = new List<Guid>
		{
			await CloneAttemptAsync(seed, "dispatching", x =>
			{
				x.ClaimToken = x.DispatchToken = Guid.NewGuid(); x.LeaseExpiresAt = now.AddMinutes(2); x.CompletedAt = null;
			})
		};
		foreach (string state in new[] { "applied", "accepted", "dismissed", "failed", "indeterminate", "stale" })
		{
			kept.Add(await CloneAttemptAsync(seed, state));
		}
		List<CategoryAssistanceAttemptEntity> before;
		await using (var db = fixture.CreateDbContext())
		{
			before = await db.CategoryAssistanceAttempts.AsNoTracking().Where(x => kept.Contains(x.Id)).ToListAsync();
		}

		await SaveSettingsAsync(seed, s => s with { DailyLimit = s.DailyLimit + 1 });

		await using var verify = fixture.CreateDbContext();
		foreach (var row in await verify.CategoryAssistanceAttempts.AsNoTracking().Where(x => retired.Contains(x.Id)).ToListAsync())
		{
			row.State.Should().Be("stale");
			row.ClaimToken.Should().BeNull();
			row.LeaseExpiresAt.Should().BeNull();
			row.NextEligibleAt.Should().BeNull();
			row.CompletedAt.Should().NotBeNull().And.BeOnOrAfter(now.AddSeconds(-1));
		}
		(await verify.CategoryAssistanceAttempts.AsNoTracking().Where(x => kept.Contains(x.Id)).ToListAsync())
			.Should().BeEquivalentTo(before, "a settings save leaves started and decided attempts alone");
	}

	[Fact]
	public async Task SettingsSaveFreesPendingSlotsHeldByUndecidedResults()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95, pendingLimit: 2);
		await CloneAttemptAsync(seed, "abstained");
		await AddItemAsync(seed, description: "Dish soap", attach: false);
		var dispatch = Dispatch();
		(await dispatch.ClaimQueuedAsync(default)).Should().BeNull("a suggestion and an abstention fill the pending limit");

		await SaveSettingsAsync(seed, s => s with { DailyLimit = s.DailyLimit + 1 });
		Guid soap = await AddItemAsync(seed, description: "Dish soap", attach: false);

		var work = await dispatch.ClaimQueuedAsync(default);
		work.Should().NotBeNull("results of the old settings revision no longer hold pending slots");
		work!.ItemId.Should().Be(soap);
	}

	[Fact]
	public async Task AtThePendingLimitAKnownSourceStillAttachesBehindAWaitingNewSource()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95, pendingLimit: 1);
		Guid soap = await AddItemAsync(seed, description: "Dish soap", attach: false);
		Guid bread = await AddItemAsync(seed, attach: false);
		var dispatch = Dispatch();

		(await dispatch.ClaimQueuedAsync(default)).Should().BeNull("the seeded suggestion fills the pending limit");

		await using (var db = fixture.CreateDbContext())
		{
			(await db.CategoryAssistanceObservations.AsNoTracking().SingleAsync(x => x.ReceiptItemId == bread)).AttemptId.Should().Be(seed.Attempt);
			(await db.CategoryAssistanceObservations.AsNoTracking().SingleAsync(x => x.ReceiptItemId == soap)).Should()
				.Match<CategoryAssistanceObservationEntity>(x => x.AttemptId == null && !x.DispatchSkipped, "a new source waits for a slot");
			(await db.CategoryAssistanceAttempts.CountAsync()).Should().Be(1);
		}

		// Once the suggestion is written its slot frees, and the waiting source is asked.
		(await Writer().ApplyNextAsync()).Should().BeTrue();
		(await CurrentAsync(bread)).Source.Should().Be("jev");
		var work = await dispatch.ClaimQueuedAsync(default);
		work.Should().NotBeNull();
		work!.ItemId.Should().Be(soap);
	}

	[Fact]
	public async Task AttachableSaveBeyondTheFirstWindowStillAttaches()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95, pendingLimit: 1);
		var waiting = new List<Guid>();
		for (int i = 0; i < 65; i++) { waiting.Add(await AddItemAsync(seed, description: $"Filler {i}", attach: false)); }
		Guid bread = await AddItemAsync(seed, attach: false);

		(await Dispatch().ClaimQueuedAsync(default)).Should().BeNull();

		await using var db = fixture.CreateDbContext();
		(await db.CategoryAssistanceObservations.AsNoTracking().SingleAsync(x => x.ReceiptItemId == bread)).AttemptId.Should().Be(seed.Attempt);
		(await db.CategoryAssistanceObservations.CountAsync(x => waiting.Contains(x.ReceiptItemId) && x.AttemptId == null && !x.DispatchSkipped))
			.Should().Be(65);
	}

	private CategoryAssistanceDispatchStore Dispatch() => new(new Factory(fixture), TimeProvider.System);

	private async Task<CategoryAssistanceSettings> SaveSettingsAsync(Seed seed, Func<CategoryAssistanceSettings, CategoryAssistanceSettings> change)
	{
		var store = new CategoryAssistanceSettingsStore(new Factory(fixture), TimeProvider.System);
		var current = await store.GetAsync();
		return await store.UpdateAsync(seed.Actor, current.Revision, change(current));
	}

	private async Task<CategoryAssistanceCompletion> SeededCompletionAsync(Seed seed)
	{
		await using var db = fixture.CreateDbContext();
		var attempt = await db.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.Attempt);
		return JsonSerializer.Deserialize<CategoryAssistanceCompletion>(attempt.ResultJson!)!;
	}

	/// <summary>A copy of the seeded attempt for another source, in the given state.</summary>
	private async Task<Guid> CloneAttemptAsync(Seed seed, string state, Action<CategoryAssistanceAttemptEntity>? change = null)
	{
		await using var db = fixture.CreateDbContext();
		var row = await db.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.Attempt);
		row.Id = Guid.NewGuid();
		row.RequestId = Guid.NewGuid();
		row.SourceHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
		row.State = state;
		change?.Invoke(row);
		db.CategoryAssistanceAttempts.Add(row);
		await db.SaveChangesAsync();
		return row.Id;
	}
}
