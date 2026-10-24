using Application.Normalization;
using Application.NormalizationAi;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.NormalizationAi;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationAiWorkflowTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Inbox_ConnectionOrOwnerChangeRevokesConsentAndQueuedAssistance(bool ownerChanged)
	{
		var ids = await InboxSeedAsync();
		var inbox = Inbox();
		await inbox.UpdatePreferencesAsync(Owner, new(true, "test-model", 10, Guid.Empty), CancellationToken.None);
		await inbox.ReconcileAsync(CancellationToken.None);
		await inbox.ScheduleNextAsync(CancellationToken.None);
		if (ownerChanged)
		{
			var changed = new NormalizationReviewInboxService(new Factory(fixture), Service(), Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = "different-owner" }), TimeProvider.System);
			await changed.ReconcileAsync(CancellationToken.None);
		}
		else { await Service().BeginConnectionChangeAsync(CancellationToken.None); }
		await using var db = fixture.CreateDbContext();
		(await db.NormalizationAssistancePreferences.SingleAsync()).Enabled.Should().BeFalse();
		var terminal = await db.NormalizationAiRuns.SingleAsync();
		terminal.State.Should().Be("cancelled");
		terminal.CompletedAt.Should().NotBeNull();
		terminal.ClaimToken.Should().BeNull();
		terminal.LeaseExpiresAt.Should().BeNull();
		(await inbox.ScheduleNextAsync(CancellationToken.None)).Should().BeFalse();
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
		await AssertUnchangedAsync(ids);
	}

	[Fact]
	public async Task Inbox_ManualSourceAdmissionStillWorksWithoutAutomaticConsent()
	{
		await InboxSeedAsync();
		var run = await Service().StartSourcesAsync(Owner, Guid.NewGuid(), [new(new("Synthetic store", "Milk", null), true, false)], "test-model", CancellationToken.None);
		run.State.Should().Be("queued");
		await using var check = fixture.CreateDbContext();
		(await check.NormalizationAssistanceAttempts.AnyAsync()).Should().BeFalse();
		(await check.NormalizationAssistancePreferences.AnyAsync()).Should().BeFalse();
	}
	private sealed class InboxClock(DateTimeOffset now) : TimeProvider
	{
		public DateTimeOffset Now { get; set; } = now;
		public override DateTimeOffset GetUtcNow() => Now;
	}

	[Fact]
	public async Task Inbox_DailyLimitSerializesConcurrentAdmissionsAndResetsAtUtcMidnight()
	{
		await InboxSeedAsync();
		var clock = new InboxClock(new DateTimeOffset(2026, 10, 23, 23, 59, 59, TimeSpan.Zero));
		NormalizationReviewInboxService NewInbox() => new(new Factory(fixture), Service(),
			Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = Owner }), clock);
		var inbox = NewInbox();
		await inbox.UpdatePreferencesAsync(Owner, new(true, "test-model", 1, Guid.Empty), CancellationToken.None);
		await inbox.ReconcileAsync(CancellationToken.None);
		await Task.WhenAll(NewInbox().ScheduleNextAsync(CancellationToken.None), NewInbox().ScheduleNextAsync(CancellationToken.None));
		await using (var check = fixture.CreateDbContext())
		{
			(await check.NormalizationAssistanceAttempts.CountAsync()).Should().Be(1);
			(await check.NormalizationAiRuns.CountAsync()).Should().Be(1);
		}
		clock.Now = clock.Now.AddSeconds(1);
		(await NewInbox().ScheduleNextAsync(CancellationToken.None)).Should().BeTrue();
		await using var after = fixture.CreateDbContext();
		(await after.NormalizationAssistanceAttempts.CountAsync()).Should().Be(2);
		(await after.NormalizationAiRuns.CountAsync()).Should().Be(2);
	}
	[Fact]
	public async Task Inbox_AutomaticRepresentativeSkipsUntouchedHistoricalRowInSameSource()
	{
		var ids = await InboxSeedAsync();
		var legacyId = Guid.Parse("00000000-0000-0000-0000-000000000001");
		Guid enrolledId;
		await using (var db = fixture.CreateDbContext())
		{
			var enrolled = await db.ReceiptItems.SingleAsync(x => x.Id == ids[0]);
			enrolledId = enrolled.Id;
			db.ReceiptItems.Add(new ReceiptItemEntity
			{
				Id = legacyId,
				ReceiptId = enrolled.ReceiptId,
				Description = enrolled.Description,
				Quantity = 1,
				UnitPrice = 1,
				TotalAmount = 1,
				Category = "Food",
				UnitPriceCurrency = enrolled.UnitPriceCurrency,
				TotalAmountCurrency = enrolled.TotalAmountCurrency
			});
			await db.SaveChangesAsync();
			await db.ReceiptItems.Where(x => x.Id == legacyId).ExecuteUpdateAsync(x => x.SetProperty(i => i.NormalizationSourceHash, (string?)null));
			await db.NormalizationReviewWork.Where(x => x.ItemId == legacyId || x.ItemId == ids[1]).ExecuteDeleteAsync();
			await db.ReceiptItems.Where(x => x.Id == ids[1]).ExecuteUpdateAsync(x => x.SetProperty(i => i.NormalizationSourceHash, (string?)null));
		}
		var inbox = Inbox();
		await inbox.UpdatePreferencesAsync(Owner, new(true, "test-model", 10, Guid.Empty), CancellationToken.None);
		await inbox.ReconcileAsync(CancellationToken.None);
		await inbox.ScheduleNextAsync(CancellationToken.None);
		await using var check = fixture.CreateDbContext();
		var attempt = await check.NormalizationAssistanceAttempts.SingleAsync();
		attempt.RunId.Should().NotBeNull();
		(await check.NormalizationAiRuns.SingleAsync()).State.Should().Be("queued");
		(await check.NormalizationReviewWork.AnyAsync(x => x.ItemId == legacyId)).Should().BeFalse();
		(await check.ReceiptItems.SingleAsync(x => x.Id == legacyId)).NormalizationSourceHash.Should().BeNull();
		(await check.NormalizationReviewWork.AnyAsync(x => x.ItemId == enrolledId)).Should().BeTrue();
	}
	[Fact]
	public async Task Inbox_SourceChangedAfterQueueCannotReachInference()
	{
		await InboxSeedAsync();
		var inbox = Inbox();
		await inbox.UpdatePreferencesAsync(Owner, new(true, "test-model", 10, Guid.Empty), CancellationToken.None);
		await inbox.ReconcileAsync(CancellationToken.None);
		await inbox.ScheduleNextAsync(CancellationToken.None);
		await using (var db = fixture.CreateDbContext())
		{
			var attempt = await db.NormalizationAssistanceAttempts.SingleAsync();
			var item = await db.ReceiptItems.SingleAsync(x => x.Description == attempt.Description);
			item.Description = "A different product";
			await db.SaveChangesAsync();
		}
		await Service().ProcessNextAsync(CancellationToken.None);
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
		await using var check = fixture.CreateDbContext();
		(await check.NormalizationAiRuns.SingleAsync()).State.Should().Be("cancelled");
	}

	[Fact]
	public async Task Inbox_ExpiredFifthAdmissionLeaseDoesNotContactProviderAgain()
	{
		await InboxSeedAsync();
		runner.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptRunnerStatus(false));
		var inbox = Inbox();
		await inbox.UpdatePreferencesAsync(Owner, new(true, "test-model", 10, Guid.Empty), CancellationToken.None);
		await inbox.ReconcileAsync(CancellationToken.None);
		await inbox.ScheduleNextAsync(CancellationToken.None);
		await using (var db = fixture.CreateDbContext())
		{
			await db.NormalizationAssistanceAttempts.ExecuteUpdateAsync(x => x.SetProperty(a => a.State, "admitting").SetProperty(a => a.AdmissionAttempts, 5).SetProperty(a => a.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
		}
		runner.Invocations.Clear();
		await Inbox().ScheduleNextAsync(CancellationToken.None);
		runner.Invocations.Should().BeEmpty("an exhausted admission budget needs explicit human retry even after a process crash");
		await using var check = fixture.CreateDbContext();
		var attempt = await check.NormalizationAssistanceAttempts.SingleAsync();
		attempt.AdmissionAttempts.Should().Be(5);
		attempt.State.Should().Be("paused");
	}
	private NormalizationReviewInboxService Inbox() => new(new Factory(fixture), Service(),
		Options.Create(new NormalizationAiOptions { Enabled = true, OwnerUserId = Owner }), TimeProvider.System);

	private async Task<Guid[]> InboxSeedAsync()
	{
		var ids = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		await db.NormalizationAssistanceAttempts.ExecuteDeleteAsync();
		await db.NormalizationAssistancePreferences.ExecuteDeleteAsync();
		return ids;
	}

	[Fact]
	public async Task Inbox_DisabledAssistanceKeepsManualWorkWithoutContactingProvider()
	{
		var ids = await InboxSeedAsync();
		runner.Invocations.Clear();
		var inbox = Inbox();
		await inbox.ReconcileAsync(CancellationToken.None);
		(await inbox.ScheduleNextAsync(CancellationToken.None)).Should().BeFalse();
		var page = await inbox.ListAsync(Owner, null, 0, 10, CancellationToken.None);
		page.Total.Should().Be(2);
		page.Items.Should().OnlyContain(x => x.AssistanceState == "manual" && x.PurchaseCount == 1);
		page.Items.SelectMany(x => x.Receipts).Select(x => x.ItemId).Should().BeEquivalentTo(ids);
		runner.Invocations.Should().BeEmpty();
		await AssertUnchangedAsync(ids);
	}

	[Fact]
	public async Task Inbox_RestartRepairsMissingAndDirtyMarkersWithoutApprovingPurchases()
	{
		var ids = await InboxSeedAsync();
		await using (var db = fixture.CreateDbContext())
		{
			await db.NormalizationReviewWork.Where(x => x.ItemId == ids[0]).ExecuteDeleteAsync();
			await db.NormalizationReviewWork.Where(x => x.ItemId == ids[1]).ExecuteUpdateAsync(x => x.SetProperty(w => w.SourceHash, (string?)null));
		}
		await Inbox().ReconcileAsync(CancellationToken.None);
		await Inbox().ReconcileAsync(CancellationToken.None);
		await using var check = fixture.CreateDbContext();
		var markers = await check.NormalizationReviewWork.Where(x => ids.Contains(x.ItemId)).ToListAsync();
		markers.Should().HaveCount(2).And.OnlyContain(x => x.SourceHash != null);
		var rows = await check.ReceiptItems.Where(x => ids.Contains(x.Id)).ToListAsync();
		rows.Should().OnlyContain(x => x.NormalizationReviewState == NormalizationReviewState.NeedsReview);
		markers.Should().OnlyContain(x => rows.Any(row => row.Id == x.ItemId && row.NormalizationRevision == x.SourceRevision));
		await AssertUnchangedAsync(ids);
	}

	[Fact]
	public async Task Inbox_ConsentRevisionRejectsStaleSettingsAndDisableCancelsQueuedRun()
	{
		var ids = await InboxSeedAsync();
		var inbox = Inbox();
		var enabled = await inbox.UpdatePreferencesAsync(Owner, new(true, "test-model", 10, Guid.Empty), CancellationToken.None);
		Func<Task> stale = () => inbox.UpdatePreferencesAsync(Owner, new(false, null, 10, Guid.Empty), CancellationToken.None);
		(await stale.Should().ThrowAsync<NormalizationCatalogException>()).Which.Code.Should().Be("preferences_changed");
		await inbox.ReconcileAsync(CancellationToken.None);
		(await inbox.ScheduleNextAsync(CancellationToken.None)).Should().BeTrue();
		await inbox.UpdatePreferencesAsync(Owner, new(false, "test-model", 10, enabled.Revision), CancellationToken.None);
		await using var db = fixture.CreateDbContext();
		var terminal = await db.NormalizationAiRuns.SingleAsync();
		terminal.State.Should().Be("cancelled");
		terminal.CompletedAt.Should().NotBeNull();
		terminal.ClaimToken.Should().BeNull();
		terminal.LeaseExpiresAt.Should().BeNull();
		(await inbox.ScheduleNextAsync(CancellationToken.None)).Should().BeFalse();
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
		await AssertUnchangedAsync(ids);
	}

	[Fact]
	public async Task Inbox_ConcurrentSchedulersCoalesceEachSourceAndUnknownRunIsNotReplayed()
	{
		await InboxSeedAsync();
		var inbox = Inbox();
		await inbox.UpdatePreferencesAsync(Owner, new(true, "test-model", 10, Guid.Empty), CancellationToken.None);
		await inbox.ReconcileAsync(CancellationToken.None);
		await Task.WhenAll(Inbox().ScheduleNextAsync(CancellationToken.None), Inbox().ScheduleNextAsync(CancellationToken.None));
		await using (var db = fixture.CreateDbContext())
		{
			var attempts = await db.NormalizationAssistanceAttempts.ToListAsync();
			attempts.Should().HaveCount(2).And.OnlyContain(x => x.RunId != null);
			attempts.Select(x => x.SourceHash).Should().OnlyHaveUniqueItems();
			(await db.NormalizationAiRuns.CountAsync()).Should().Be(2);
			await db.NormalizationAiRuns.ExecuteUpdateAsync(x => x.SetProperty(run => run.State, "unknown"));
		}
		(await Inbox().ScheduleNextAsync(CancellationToken.None)).Should().BeFalse();
		var page = await Inbox().ListAsync(Owner, null, 0, 10, CancellationToken.None);
		page.Items.Should().OnlyContain(x => x.AssistanceState == "unknown");
		runner.Verify(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}
