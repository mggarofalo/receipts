using Application.Classification;
using Application.Taxonomy;
using Common;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed partial class CategoryAssistanceHistoricalRequestStoreTests(PostgresFixture server)
{
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }

	private static CategoryAssistanceHistoricalRequestStore Store(PostgresFixture fixture,
		CategoryAssistanceCapabilityState state = CategoryAssistanceCapabilityState.Ready)
	{
		var capability = new Mock<ICategoryAssistanceCapabilitySource>();
		capability.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
			new CategoryAssistanceCapability("jev", state, ["jev-1.14.0"]));
		return new(new Factory(fixture), capability.Object, TimeProvider.System);
	}

	private static async Task<(string Actor, Guid Taxonomy, Guid First, Guid Other)> SeedAsync(PostgresFixture fixture, bool archived = false)
	{
		string actor = "historical-assistance-" + Guid.NewGuid();
		Guid first = Guid.NewGuid(), other = Guid.NewGuid();
		await using var db = fixture.CreateDbContext();
		db.Users.Add(new ApplicationUser { Id = actor, UserName = actor });
		var head = await db.TaxonomyHeads.SingleAsync();
		var taxonomy = Guid.NewGuid();
		var category = Guid.NewGuid();
		db.TaxonomyRevisions.Add(new TaxonomyRevisionEntity
		{
			Id = taxonomy,
			Sequence = 1,
			PreviousRevisionId = head.RevisionId,
			Actor = actor,
			Reason = "Historical assistance test",
			OperationId = Guid.NewGuid(),
			RecordedAt = DateTimeOffset.UtcNow
		});
		db.TaxonomyIdentities.Add(new TaxonomyIdentityEntity { Id = category });
		db.TaxonomyNodes.Add(new TaxonomyNodeEntity { RevisionId = taxonomy, CategoryId = category, Name = "Food", Archived = archived });
		head.RevisionId = taxonomy;
		var settings = await db.CategoryAssistanceSettings.SingleAsync();
		settings.Enabled = true; settings.Provider = "jev"; settings.Model = "jev-1.14.0";
		settings.PendingLimit = 3; settings.DailyLimit = 10; settings.Revision = Guid.NewGuid();
		var connection = await db.CategoryAssistanceConnection.SingleAsync();
		connection.Provider = "jev"; connection.Model = "jev-1.14.0";
		connection.Generation = Guid.NewGuid(); connection.Changing = false;
		connection.ConfigurationFingerprint = new string('A', 64);
		foreach (var (itemId, description) in new[] { (first, " milk "), (other, "milk") })
		{
			var receiptId = Guid.NewGuid();
			db.Receipts.Add(new ReceiptEntity
			{
				Id = receiptId,
				Location = "Shop",
				Date = new DateOnly(2020, 1, 1),
				TaxAmountCurrency = Currency.USD
			});
			db.ReceiptItems.Add(new ReceiptItemEntity
			{
				Id = itemId,
				ReceiptId = receiptId,
				Description = description,
				Category = "",
				UnitPriceCurrency = Currency.USD,
				TotalAmountCurrency = Currency.USD
			});
		}


		await db.SaveChangesAsync();
		return (actor, taxonomy, first, other);
	}

	[Fact]
	public async Task UnlinkedMarkerMatchingObsoleteAttemptDoesNotReserveCapacityOrResend()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var store = Store(fixture);
		var first = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", " milk ", null), new HistoricalGroupKey("Shop", "milk", null)]);
		await store.EnrollAsync(seed.Actor, first, default);
		var dispatch = new CategoryAssistanceDispatchStore(new Factory(fixture), TimeProvider.System);
		var oldWork = (await dispatch.ClaimQueuedAsync(default))!;
		await using (var db = fixture.CreateDbContext())
		{
			var markers = await db.CategoryAssistanceObservations.Where(x => x.SaveId == first.OperationId).ToListAsync();
			markers.Should().HaveCount(2).And.OnlyContain(x => x.AttemptId == oldWork.AttemptId);
			markers.Single(x => x.ReceiptItemId == seed.Other).AttemptId = null;
			var attempt = await db.CategoryAssistanceAttempts.SingleAsync(x => x.Id == oldWork.AttemptId);
			attempt.QuestionPolicyVersion = "obsolete"; attempt.State = "suggested";
			(await db.CategoryAssistanceSettings.SingleAsync()).PendingLimit = 1;
			await db.SaveChangesAsync();
		}
		var next = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", "milk", null)]);
		(await store.EnrollAsync(seed.Actor, next, default)).ItemCount.Should().Be(1);
		var newWork = (await dispatch.ClaimQueuedAsync(default))!;
		newWork.AttemptId.Should().NotBe(oldWork.AttemptId);
		await using var verify = fixture.CreateDbContext();
		(await verify.CategoryAssistanceObservations.SingleAsync(x => x.SaveId == first.OperationId && x.ReceiptItemId == seed.Other))
			.AttemptId.Should().Be(oldWork.AttemptId);
		(await verify.CategoryAssistanceAttempts.CountAsync()).Should().Be(2);
	}

	[Theory]
	[InlineData("queued")]
	[InlineData("dispatching")]
	[InlineData("suggested")]
	[InlineData("abstained")]
	public async Task ObsoleteResultIsStaleAndDoesNotBlockNewHistoricalRequest(string oldState)
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var store = Store(fixture);
		var first = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", " milk ", null)]);
		await store.EnrollAsync(seed.Actor, first, default);
		var dispatch = new CategoryAssistanceDispatchStore(new Factory(fixture), TimeProvider.System);
		var work = (await dispatch.ClaimQueuedAsync(default))!;
		await using (var db = fixture.CreateDbContext())
		{
			var row = await db.CategoryAssistanceAttempts.SingleAsync(x => x.Id == work.AttemptId);
			row.QuestionPolicyVersion = "obsolete"; row.State = oldState;
			(await db.CategoryAssistanceSettings.SingleAsync()).PendingLimit = 1;
			await db.SaveChangesAsync();
		}
		var progress = await store.GetAsync(seed.Actor, first.OperationId, default);
		progress.State.Should().Be("failed");
		progress.Groups.Single().States.Should().Equal("stale");
		var second = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", "milk", null)]);
		(await store.EnrollAsync(seed.Actor, second, default)).ItemCount.Should().Be(1);
		(await dispatch.ClaimQueuedAsync(default)).Should().NotBeNull();
	}

	[Fact]
	public async Task UnsupportedActiveTreePersistsScopedFailureWithoutDailyDebit()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture, archived: true);
		var request = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
				[new HistoricalGroupKey("Shop", " milk ", null)]);
		var store = Store(fixture);
		await store.EnrollAsync(seed.Actor, request, default);
		var dispatch = new CategoryAssistanceDispatchStore(new Factory(fixture), TimeProvider.System);
		(await dispatch.ClaimQueuedAsync(default)).Should().BeNull();
		var progress = await store.GetAsync(seed.Actor, request.OperationId, default);
		progress.State.Should().Be("failed");
		progress.Groups.Single().States.Should().Equal("failed");
		progress.Groups.Single().FailureReasons.Should().Equal("InvalidInput");
		Func<Task> otherActor = () => store.GetAsync("someone else", request.OperationId, default);
		await otherActor.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 404);
		await using var verify = fixture.CreateDbContext();
		(await verify.CategoryAssistanceAttempts.SingleAsync()).DebitUtcDay.Should().BeNull();
		(await verify.CategoryAssistanceDailyUsage.CountAsync()).Should().Be(0);
		var activity = await new CategoryAssistanceReadStore(new Factory(fixture), TimeProvider.System)
				.GetActivityAsync(default);
		activity.RecentFailures.Should().ContainSingle(x => x.Failure == "InvalidInput");
	}

	[Fact]
	public async Task ExactRawGroupEnrollmentIsDurableActorScopedAndReplaySafe()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var store = Store(fixture);
		var request = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", " milk ", null)]);
		var first = await store.EnrollAsync(seed.Actor, request, default);
		first.ItemCount.Should().Be(1);
		first.Groups.Single().ItemCount.Should().Be(1);
		(await store.EnrollAsync(seed.Actor, request, default)).Should().BeEquivalentTo(first);
		(await store.ListAsync(seed.Actor, 0, 25, default)).Items.Should().ContainSingle();
		Func<Task> otherActor = () => store.GetAsync("another actor", request.OperationId, default);
		await otherActor.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 404);
		Func<Task> conflict = () => store.EnrollAsync(seed.Actor,
			request with { Groups = [new HistoricalGroupKey("Shop", "milk", null)] }, default);
		await conflict.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 409);
		Func<Task> sameIdOtherActor = () => store.EnrollAsync("another actor", request, default);
		await sameIdOtherActor.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 409);

		await using var db = fixture.CreateDbContext();
		var markers = await db.CategoryAssistanceObservations.Where(x => x.SaveId == request.OperationId).ToListAsync();
		markers.Should().ContainSingle().Which.ReceiptItemId.Should().Be(seed.First);
		markers.Single().Evidence.Should().Be("explicit_history_request");
		(await db.CategoryAssistanceObservations.AnyAsync(x => x.ReceiptItemId == seed.Other)).Should().BeFalse();
	}

	[Fact]
	public async Task CapabilityRejectsButExplicitManualChoiceEnrollsAndLaterEditInvalidates()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var request = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", " milk ", null)]);
		Func<Task> unavailable = () => Store(fixture, CategoryAssistanceCapabilityState.Unavailable)
			.EnrollAsync(seed.Actor, request, default);
		await unavailable.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 409);
		await using (var db = fixture.CreateDbContext())
		{
			var assignment = new PurchaseClassificationEntity
			{
				Id = Guid.NewGuid(),
				ReceiptItemId = seed.First,
				Sequence = 1,
				TaxonomyRevisionId = seed.Taxonomy,
				Source = "manual",
				Actor = seed.Actor,
				OperationId = Guid.NewGuid(),
				RecordedAt = DateTimeOffset.UtcNow
			};
			db.Classifications.Add(assignment);
			db.CurrentClassifications.Add(new CurrentClassificationEntity
			{ ReceiptItemId = seed.First, AssignmentId = assignment.Id });
			await db.SaveChangesAsync();
		}
		var enrolled = await Store(fixture).EnrollAsync(seed.Actor, request, default);
		enrolled.ItemCount.Should().Be(1);
		var dispatch = new CategoryAssistanceDispatchStore(new Factory(fixture), TimeProvider.System);
		var queued = await dispatch.ClaimQueuedAsync(default);
		queued.Should().NotBeNull("an explicit historical request may include the captured manual classification");
		await using (var db = fixture.CreateDbContext())
		{
			var revised = new PurchaseClassificationEntity
			{
				Id = Guid.NewGuid(),
				ReceiptItemId = seed.First,
				Sequence = 2,
				TaxonomyRevisionId = seed.Taxonomy,
				Source = "manual",
				Actor = seed.Actor,
				OperationId = Guid.NewGuid(),
				RecordedAt = DateTimeOffset.UtcNow
			};
			db.Classifications.Add(revised);
			(await db.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == seed.First)).AssignmentId = revised.Id;
			await db.SaveChangesAsync();
		}
		(await dispatch.TryStartDispatchAsync(queued!,
			new CategoryAssistanceCapability("jev", CategoryAssistanceCapabilityState.Ready, ["jev-1.14.0"]),
			DateTimeOffset.UtcNow, default)).Should().BeNull();
		await using var verify = fixture.CreateDbContext();
		(await verify.CategoryAssistanceHistoricalRequests.CountAsync()).Should().Be(1);
		(await verify.CategoryAssistanceDailyUsage.CountAsync()).Should().Be(0);
	}

	[Fact]
	public async Task SelectedGroupOverOneHundredPurchasesFailsAtomically()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		await using (var db = fixture.CreateDbContext())
		{
			var receiptId = await db.ReceiptItems.Where(x => x.Id == seed.First)
				.Select(x => x.ReceiptId).SingleAsync();
			for (int i = 0; i < 100; i++)
			{
				db.ReceiptItems.Add(new ReceiptItemEntity
				{
					Id = Guid.NewGuid(),
					ReceiptId = receiptId,
					Description = " milk ",
					Category = "",
					UnitPriceCurrency = Currency.USD,
					TotalAmountCurrency = Currency.USD
				});
			}

			await db.SaveChangesAsync();
		}
		var request = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", " milk ", null)]);
		Func<Task> overLimit = () => Store(fixture).EnrollAsync(seed.Actor, request, default);
		await overLimit.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 400);
		await using var verify = fixture.CreateDbContext();
		(await verify.CategoryAssistanceHistoricalRequests.CountAsync()).Should().Be(0);
		(await verify.CategoryAssistanceObservations.CountAsync()).Should().Be(0);
	}

	[Fact]
	public async Task UnclaimedHistoricalRequestsReservePendingCapacityAcrossOperations()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		await using (var db = fixture.CreateDbContext())
		{
			(await db.CategoryAssistanceSettings.SingleAsync()).PendingLimit = 2;
			await db.SaveChangesAsync();
		}
		var store = Store(fixture);
		var first = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", " milk ", null)]);
		var normalizedDuplicate = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", "milk", null)]);
		(await store.EnrollAsync(seed.Actor, first, default)).ItemCount.Should().Be(1);
		(await store.EnrollAsync(seed.Actor, normalizedDuplicate, default)).ItemCount.Should().Be(1,
			"another campaign reserves independent capacity for the same normalized source");

		await using (var db = fixture.CreateDbContext())
		{
			(await db.ReceiptItems.SingleAsync(x => x.Id == seed.Other)).Description = "eggs";
			await db.SaveChangesAsync();
		}
		var distinct = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", "eggs", null)]);
		Func<Task> overBudget = () => store.EnrollAsync(seed.Actor, distinct, default);
		await overBudget.Should().ThrowAsync<TaxonomyException>().Where(x => x.Status == 409);
		await using var verify = fixture.CreateDbContext();
		(await verify.CategoryAssistanceHistoricalRequests.CountAsync()).Should().Be(2);
		(await verify.CategoryAssistanceObservations.CountAsync()).Should().Be(2);
	}

	[Fact]
	public async Task TerminalEarlierCampaignDoesNotReviewNewRawGroup()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var store = Store(fixture);
		var first = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", " milk ", null)]);
		await store.EnrollAsync(seed.Actor, first, default);
		var dispatch = new CategoryAssistanceDispatchStore(new Factory(fixture), TimeProvider.System);
		var queued = await dispatch.ClaimQueuedAsync(default);
		queued.Should().NotBeNull();
		await using (var db = fixture.CreateDbContext())
		{
			(await db.CategoryAssistanceAttempts.SingleAsync(x => x.Id == queued!.AttemptId)).State = "accepted";
			await db.SaveChangesAsync();
		}

		var anotherRawGroup = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", "milk", null)]);
		await store.EnrollAsync(seed.Actor, anotherRawGroup, default);
		var next = await dispatch.ClaimQueuedAsync(default);
		next.Should().NotBeNull();
		next!.AttemptId.Should().NotBe(queued!.AttemptId);
		await using var verify = fixture.CreateDbContext();
		(await verify.CategoryAssistanceHistoricalRequests.CountAsync()).Should().Be(2);
		var attempts = await verify.CategoryAssistanceAttempts.ToDictionaryAsync(x => x.RequestScope);
		attempts.Keys.Should().BeEquivalentTo([first.OperationId, anotherRawGroup.OperationId]);
		(await CategoryAssistanceDecisionStore.GetEligibleObservationsAsync(verify,
			attempts[anotherRawGroup.OperationId], null, default))
			.Select(x => x.ReceiptItemId).Should().Equal(seed.Other);
	}

	[Fact]
	public async Task AutosaveAndAnotherCampaignCannotEnterHistoricalReviewSet()
	{
		await using var fixture = await server.CreateIsolatedAsync();
		var seed = await SeedAsync(fixture);
		var store = Store(fixture);
		var first = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", " milk ", null)]);
		var second = new CategoryAssistanceHistoricalRequest(Guid.NewGuid(), seed.Taxonomy,
			[new HistoricalGroupKey("Shop", "milk", null)]);
		await store.EnrollAsync(seed.Actor, first, default);
		await store.EnrollAsync(seed.Actor, second, default);
		await using (var db = fixture.CreateDbContext())
		{
			var historical = await db.CategoryAssistanceObservations.AsNoTracking()
				.SingleAsync(x => x.SaveId == first.OperationId);
			db.CategoryAssistanceObservations.Add(new()
			{
				SaveId = Guid.NewGuid(),
				ReceiptItemId = historical.ReceiptItemId,
				ReceiptId = historical.ReceiptId,
				Actor = seed.Actor,
				Location = historical.Location,
				Description = historical.Description,
				ItemCode = historical.ItemCode,
				SourceHash = historical.SourceHash,
				ItemRevision = historical.ItemRevision,
				ReceiptRevision = historical.ReceiptRevision,
				AssignmentId = historical.AssignmentId,
				SettingsRevision = historical.SettingsRevision,
				RestoreGeneration = historical.RestoreGeneration,
				TaxonomyRevision = historical.TaxonomyRevision,
				Evidence = "post_consent_no_choice_save",
				RecordedAt = DateTimeOffset.UtcNow
			});
			await db.SaveChangesAsync();
		}
		var dispatch = new CategoryAssistanceDispatchStore(new Factory(fixture), TimeProvider.System);
		for (int i = 0; i < 3; i++)
		{
			(await dispatch.ClaimQueuedAsync(default)).Should().NotBeNull();
		}

		await using var verify = fixture.CreateDbContext();
		var attempts = await verify.CategoryAssistanceAttempts.ToDictionaryAsync(x => x.RequestScope);
		attempts.Keys.Should().BeEquivalentTo([Guid.Empty, first.OperationId, second.OperationId]);
		(await CategoryAssistanceDecisionStore.GetEligibleObservationsAsync(verify,
			attempts[first.OperationId], null, default)).Select(x => x.ReceiptItemId).Should().Equal(seed.First);
		(await CategoryAssistanceDecisionStore.GetEligibleObservationsAsync(verify,
			attempts[second.OperationId], null, default)).Select(x => x.ReceiptItemId).Should().Equal(seed.Other);
	}
}
