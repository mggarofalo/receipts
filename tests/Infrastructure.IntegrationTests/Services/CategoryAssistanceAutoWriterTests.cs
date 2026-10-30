using System.Text.Json;
using Application.Classification;
using Application.Normalization;
using Application.Taxonomy;
using Common;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed partial class CategoryAssistanceAutoWriterTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	private const string Store = "Corner Shop";
	private const string Description = "Bread loaf";

	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }

	private sealed class ActorAccessor(string actor) : Application.Interfaces.Services.ICurrentUserAccessor
	{
		public string? UserId => actor;
		public Guid? ApiKeyId => null;
		public string? IpAddress => null;
		public string? UserAgent => null;
	}

	private sealed class ActorFactory(PostgresFixture fixture, string actor) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => new(fixture.CreateOptions(), new ActorAccessor(actor)); }

	private sealed record Seed(string Actor, Guid Taxonomy, Guid Root, Guid Leaf, Guid OtherRoot, Guid Attempt,
		Guid RequestId, IReadOnlyList<Guid> Items);

	[Fact]
	public async Task StrongRootIsWrittenThroughClassificationAndListedAsUndoableJevActivity()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95);

		(await Writer().ApplyNextAsync()).Should().BeTrue();

		var assignment = await CurrentAsync(seed.Items[0]);
		assignment.CategoryId.Should().Be(seed.Root);
		assignment.Source.Should().Be("jev");
		assignment.Actor.Should().Be(seed.Actor);
		var evidence = JsonSerializer.Deserialize<JevAutoWriteEvidence>(assignment.Reason)!;
		evidence.AttemptId.Should().Be(seed.Attempt);
		evidence.RequestId.Should().Be(seed.RequestId);
		evidence.QuestionPolicy.Should().Be(CategorySuggestionPolicy.Version);
		evidence.Model.Should().Be(CategorySuggestionPolicy.Model);
		evidence.TaxonomyRevision.Should().Be(seed.Taxonomy.ToString("D"));
		evidence.Level.Should().Be(CategoryAutoWriteLevel.Root);
		evidence.FitProbability.Should().Be(0.9);
		evidence.RootProbability.Should().Be(0.95);
		evidence.LeafProbability.Should().Be(0.95);
		evidence.RootThreshold.Should().Be(0.90);
		evidence.LeafThreshold.Should().Be(0.90);
		evidence.LeafEnabled.Should().BeFalse();

		await using (var db = fixture.CreateDbContext())
		{
			(await db.ClassificationOperations.AsNoTracking()
				.SingleAsync(x => x.Actor == seed.Actor && x.OperationId == assignment.OperationId))
				.AssignmentId.Should().Be(assignment.Id);
			var attempt = await db.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.Attempt);
			attempt.State.Should().Be("applied");
			attempt.AutoWriteCheckedAt.Should().NotBeNull();
			var operation = await db.HistoricalOperations.AsNoTracking().SingleAsync();
			operation.Kind.Should().Be(HistoricalOperationKind.JevSuggestionV1);
			operation.State.Should().Be(HistoricalOperationState.Completed);
			operation.ApprovedMappingRevisionId.Should().BeNull();
			var row = await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == operation.Id);
			row.ItemId.Should().Be(seed.Items[0]);
			row.State.Should().Be(HistoricalItemState.Applied);
			row.AppliedAssignmentId.Should().Be(assignment.Id);
			row.PreviousCategoryId.Should().BeNull();
			row.TargetCategoryId.Should().Be(seed.Root);
			row.ApprovedMappingRevisionId.Should().BeNull();
		}

		var activity = await Historical().ListAutomaticAsync(seed.Actor, 0, 10);
		var summary = activity.Items.Should().ContainSingle().Subject;
		summary.Source.Should().Be("jev");
		summary.State.Should().Be(HistoricalOperationState.Completed);
		summary.Groups.Should().ContainSingle().Which.TargetCategoryId.Should().Be(seed.Root);
		(await Historical().ListAutomaticAsync("someone-else", 0, 10)).Items.Should().BeEmpty();

		// A written match leaves the review queue and is counted separately.
		var reads = new CategoryAssistanceReadStore(new Factory(fixture), TimeProvider.System);
		(await reads.ListSuggestionsAsync(0, 10, default)).Total.Should().Be(0);
		var counts = await reads.GetActivityAsync(default);
		counts.Applied.Should().Be(1);
		counts.Suggested.Should().Be(0);
	}

	[Theory]
	[InlineData(false, 0.95, true, false)]
	[InlineData(true, 0.95, true, true)]
	[InlineData(true, 0.85, true, false)]
	[InlineData(true, 0.95, false, false)]
	public async Task LeafIsWrittenOnlyWhenEnabledStrongAndUnderTheChosenRoot(bool enabled, double leaf, bool underRoot, bool expectLeaf)
	{
		var seed = await SeedAsync(root: 0.95, leaf: leaf, leafUnderRoot: underRoot);

		(await Writer(new CategoryAutoWriteOptions { LeafAutoWriteEnabled = enabled }).ApplyNextAsync()).Should().BeTrue();

		var assignment = await CurrentAsync(seed.Items[0]);
		assignment.Source.Should().Be("jev");
		assignment.CategoryId.Should().Be(expectLeaf ? seed.Leaf : seed.Root);
		var evidence = JsonSerializer.Deserialize<JevAutoWriteEvidence>(assignment.Reason)!;
		evidence.Level.Should().Be(expectLeaf ? CategoryAutoWriteLevel.Leaf : CategoryAutoWriteLevel.Root);
		evidence.LeafEnabled.Should().Be(enabled);
		evidence.LeafProbability.Should().Be(leaf);
	}

	[Fact]
	public async Task WeakRootStaysASuggestionAndIsNotReconsideredUntilANewSave()
	{
		var seed = await SeedAsync(root: 0.8, leaf: 0.95);
		var writer = Writer();

		(await writer.ApplyNextAsync()).Should().BeTrue();

		var legacy = await CurrentAsync(seed.Items[0]);
		legacy.Source.Should().Be("legacy_import");
		DateTimeOffset? checkpoint;
		await using (var db = fixture.CreateDbContext())
		{
			var attempt = await db.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.Attempt);
			attempt.State.Should().Be("suggested");
			checkpoint = attempt.AutoWriteCheckedAt;
			checkpoint.Should().NotBeNull();
			(await db.HistoricalOperations.CountAsync()).Should().Be(0);
		}
		(await writer.ApplyNextAsync()).Should().BeFalse("nothing was saved since the last check");

		await AddItemAsync(seed);
		(await writer.ApplyNextAsync()).Should().BeTrue();
		await using var verify = fixture.CreateDbContext();
		var reconsidered = await verify.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.Attempt);
		reconsidered.State.Should().Be("suggested");
		reconsidered.AutoWriteCheckedAt.Should().BeAfter(checkpoint!.Value);
		(await verify.Classifications.CountAsync(x => x.Source == "jev")).Should().Be(0);
	}

	[Fact]
	public async Task AutoApplyOffWritesNothingAndReturnsAnAppliedSourceToReview()
	{
		var off = await SeedAsync(root: 0.95, leaf: 0.95);
		await SetAutoApplyAsync(false);
		(await Writer().ApplyNextAsync()).Should().BeTrue();
		(await CurrentAsync(off.Items[0])).Source.Should().Be("legacy_import");
		await using (var db = fixture.CreateDbContext())
		{
			(await db.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == off.Attempt)).State.Should().Be("suggested");
		}

		await ReplaceDatabaseAsync();
		var seed = await SeedAsync(root: 0.95, leaf: 0.95);
		(await Writer().ApplyNextAsync()).Should().BeTrue();
		(await CurrentAsync(seed.Items[0])).Source.Should().Be("jev");
		await SetAutoApplyAsync(false);
		var later = await AddItemAsync(seed);

		(await Writer().ApplyNextAsync()).Should().BeTrue();

		(await CurrentAsync(later)).Source.Should().Be("legacy_import");
		(await CurrentAsync(seed.Items[0])).Source.Should().Be("jev", "switching off does not revert earlier writes");
		await using var verify = fixture.CreateDbContext();
		(await verify.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.Attempt)).State.Should().Be("suggested");
		(await new CategoryAssistanceReadStore(new Factory(fixture), TimeProvider.System).GetSuggestionAsync(seed.Attempt, default))
			.Should().NotBeNull();
	}

	[Theory]
	[InlineData("manual")]
	[InlineData("manual-clear")]
	[InlineData("categorized")]
	[InlineData("approved-mapping")]
	public async Task NeverOverwritesAnExistingChoice(string prior)
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95, items: 2, categorizeSecond: prior == "categorized");
		Guid protectedItem = seed.Items[1];
		switch (prior)
		{
			case "manual":
			case "manual-clear":
				var legacy = await CurrentAsync(protectedItem);
				await Classification().AssignAsync(seed.Actor, protectedItem, new(prior == "manual" ? seed.OtherRoot : null,
					seed.Taxonomy, legacy.Id, Guid.NewGuid(), "Owner's choice"));
				break;
			case "approved-mapping":
				var source = NormalizationCatalogPolicy.Normalize(new(Store, Description, null));
				await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(seed.Actor,
					new(source, NormalizationCatalogPolicy.Hash(source), null, seed.Taxonomy, seed.OtherRoot, Guid.NewGuid(), "Approved"));
				break;
		}
		var before = await CurrentAsync(protectedItem);

		(await Writer().ApplyNextAsync()).Should().BeTrue();

		(await CurrentAsync(protectedItem)).Should().BeEquivalentTo(before);
		var other = await CurrentAsync(seed.Items[0]);
		if (prior == "approved-mapping")
		{
			// The mapping covers the whole source, so neither purchase is Jev's to fill.
			other.Source.Should().Be("legacy_import");
		}
		else
		{
			other.Source.Should().Be("jev");
			other.CategoryId.Should().Be(seed.Root);
		}
		await using var db = fixture.CreateDbContext();
		(await db.HistoricalOperationItems.CountAsync(x => x.ItemId == protectedItem)).Should().Be(0);
	}

	[Theory]
	[InlineData("active", true)]
	[InlineData("left_as_is", false)]
	[InlineData("later", false)]
	public async Task SavingActorsReviewPreferenceIsRespected(string state, bool excludeSecond)
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95, items: 2);
		await using (var db = fixture.CreateDbContext())
		{
			db.CategoryAssistanceReviewPreferences.Add(new CategoryAssistanceReviewPreferenceEntity
			{
				Actor = seed.Actor,
				AttemptId = seed.Attempt,
				Location = Normalized.Location,
				Description = Normalized.Description,
				State = state,
				Revision = Guid.NewGuid(),
				ExcludedItemIdsJson = JsonSerializer.Serialize(excludeSecond ? new[] { seed.Items[1] } : [])
			});
			await db.SaveChangesAsync();
		}

		(await Writer().ApplyNextAsync()).Should().BeTrue();

		(await CurrentAsync(seed.Items[1])).Source.Should().Be("legacy_import");
		(await CurrentAsync(seed.Items[0])).Source.Should().Be(state == "active" ? "jev" : "legacy_import");
	}

	[Fact]
	public async Task EachSavingActorGetsTheirOwnActivityEntry()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95);
		string second = await UserAsync();
		var item = await AddItemAsync(seed, actor: second);

		(await Writer().ApplyNextAsync()).Should().BeTrue();

		(await CurrentAsync(seed.Items[0])).Actor.Should().Be(seed.Actor);
		(await CurrentAsync(item)).Actor.Should().Be(second);
		(await Historical().ListAutomaticAsync(seed.Actor, 0, 10)).Items.Should().ContainSingle().Which.Source.Should().Be("jev");
		(await Historical().ListAutomaticAsync(second, 0, 10)).Items.Should().ContainSingle().Which.Source.Should().Be("jev");
	}

	[Fact]
	public async Task RepeatedPassesAreIdempotentAndALaterSaveIsWrittenByALaterPass()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95);
		var writer = Writer();
		(await writer.ApplyNextAsync()).Should().BeTrue();
		var first = await CurrentAsync(seed.Items[0]);

		(await writer.ApplyNextAsync()).Should().BeFalse();
		(await writer.ApplyNextAsync()).Should().BeFalse();
		await using (var db = fixture.CreateDbContext())
		{
			(await db.Classifications.CountAsync(x => x.Source == "jev")).Should().Be(1);
			(await db.HistoricalOperations.CountAsync()).Should().Be(1);
		}

		var later = await AddItemAsync(seed);
		(await writer.ApplyNextAsync()).Should().BeTrue();

		(await CurrentAsync(later)).Should().Match<PurchaseClassificationEntity>(x => x.Source == "jev" && x.CategoryId == seed.Root);
		(await CurrentAsync(seed.Items[0])).Id.Should().Be(first.Id);
		(await writer.ApplyNextAsync()).Should().BeFalse();
		await using var verify = fixture.CreateDbContext();
		(await verify.Classifications.CountAsync(x => x.Source == "jev")).Should().Be(2);
		(await verify.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.Attempt)).State.Should().Be("applied");
		(await Historical().ListAutomaticAsync(seed.Actor, 0, 10)).Items.Should().HaveCount(2);
	}

	[Fact]
	public async Task UndoRestoresThePreviousValueAsManualAndIsNotRewritten()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95);
		var writer = Writer();
		(await writer.ApplyNextAsync()).Should().BeTrue();
		var operation = (await Historical().ListAutomaticAsync(seed.Actor, 0, 10)).Items.Single().OperationId;

		var refused = await Assert.ThrowsAsync<TaxonomyException>(() => Historical().ApplyChunkAsync(seed.Actor, operation, 25));
		refused.Status.Should().Be(409);
		var undo = await Historical().UndoChunkAsync(seed.Actor, operation, new(seed.Taxonomy, "Not bread"), 25);

		undo.Undone.Should().Be(1);
		undo.State.Should().Be(HistoricalOperationState.Undone);
		var restored = await CurrentAsync(seed.Items[0]);
		restored.Source.Should().Be("manual");
		restored.CategoryId.Should().BeNull();
		(await Historical().ListAutomaticAsync(seed.Actor, 0, 10)).Items.Single().State.Should().Be(HistoricalOperationState.Undone);

		// Another save of the same purchase reaches the applied suggestion, but a manual value is never Jev's to replace.
		await ObserveAsync(seed, seed.Items[0], seed.Actor);
		(await writer.ApplyNextAsync()).Should().BeTrue();
		(await CurrentAsync(seed.Items[0])).Id.Should().Be(restored.Id);
		await Assert.ThrowsAsync<TaxonomyException>(() => Historical().ApplyChunkAsync(seed.Actor, operation, 25));
		(await CurrentAsync(seed.Items[0])).Id.Should().Be(restored.Id);
	}

	[Fact]
	public async Task AppliedAttemptNoLongerHoldsThePendingLimit()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95, pendingLimit: 1);
		var dispatch = new CategoryAssistanceDispatchStore(new Factory(fixture), TimeProvider.System);
		Guid soap = await AddItemAsync(seed, description: "Dish soap", attach: false);
		(await dispatch.ClaimQueuedAsync(default)).Should().BeNull("the unreviewed suggestion fills the pending limit");

		(await Writer().ApplyNextAsync()).Should().BeTrue();

		var work = await dispatch.ClaimQueuedAsync(default);
		work.Should().NotBeNull();
		work!.ItemId.Should().Be(soap);
		work.Request.Store.Should().Be(Normalized.Location, "the request carries the receipt store");
	}

	[Fact]
	public async Task AFailingAttemptYieldsItsTurnSoOtherStrongMatchesAreStillWritten()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95);
		var (second, soap) = await SecondAttemptAsync(seed, "Dish soap");
		var cursor = new CategoryAssistanceAutoWriteCursor();
		// The seeded attempt sorts first; its write fails on every pass until the fault is removed.
		var failing = Writer(cursor: cursor, factory: new FaultFactory(fixture, seed.Items[0]));

		await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ApplyNextAsync());
		(await failing.ApplyNextAsync()).Should().BeTrue();

		(await CurrentAsync(soap)).Should().Match<PurchaseClassificationEntity>(x => x.Source == "jev" && x.CategoryId == seed.Root);
		(await CurrentAsync(seed.Items[0])).Source.Should().Be("legacy_import");
		await using (var db = fixture.CreateDbContext())
		{
			(await db.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.Attempt)).Should()
				.Match<CategoryAssistanceAttemptEntity>(x => x.State == "suggested" && x.AutoWriteCheckedAt == null, "the failed pass rolled back");
			(await db.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == second)).State.Should().Be("applied");
		}

		// Once the fault clears, the rotation returns to the attempt that failed and then has nothing left to do.
		var writer = Writer(cursor: cursor);
		(await writer.ApplyNextAsync()).Should().BeTrue();
		(await CurrentAsync(seed.Items[0])).Source.Should().Be("jev");
		(await writer.ApplyNextAsync()).Should().BeFalse();
		(await Historical().ListAutomaticAsync(seed.Actor, 0, 10)).Items.Should().HaveCount(2);
	}

	[Fact]
	public async Task PassesRotateThroughPendingAttemptsInIdOrder()
	{
		var seed = await SeedAsync(root: 0.95, leaf: 0.95);
		var (second, soap) = await SecondAttemptAsync(seed, "Dish soap");
		var writer = Writer();

		(await writer.ApplyNextAsync()).Should().BeTrue();
		(await CurrentAsync(seed.Items[0])).Source.Should().Be("jev", "the lower attempt ID is handled first");
		(await CurrentAsync(soap)).Source.Should().Be("legacy_import");

		(await writer.ApplyNextAsync()).Should().BeTrue();
		(await CurrentAsync(soap)).Source.Should().Be("jev");
		(await writer.ApplyNextAsync()).Should().BeFalse();
		await using var db = fixture.CreateDbContext();
		(await db.CategoryAssistanceAttempts.CountAsync(x => (x.Id == seed.Attempt || x.Id == second) && x.State == "applied")).Should().Be(2);
	}

	private sealed class FaultFactory(PostgresFixture fixture, Guid item) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
			.AddInterceptors(new FailWrite(item)).Options);
	}

	private sealed class FailWrite(Guid item) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
	{
		public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
			Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
			Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
			eventData.Context!.ChangeTracker.Entries<HistoricalOperationItemEntity>()
				.Any(x => x.State == EntityState.Added && x.Entity.ItemId == item)
				? throw new InvalidOperationException("Injected automatic write failure")
				: base.SavingChangesAsync(eventData, result, cancellationToken);
	}

	/// <summary>
	/// A second strong suggestion for another source, with an ID that sorts after the seeded attempt in PostgreSQL's
	/// uuid order, and one saved purchase attached to it.
	/// </summary>
	private async Task<(Guid Attempt, Guid Item)> SecondAttemptAsync(Seed seed, string description)
	{
		var source = NormalizationCatalogPolicy.Normalize(new(Store, description, null));
		Guid id = Guid.Parse("ffffffff" + Guid.NewGuid().ToString("N")[8..]), item = Guid.NewGuid(), receipt = Guid.NewGuid();
		await AddReceiptAsync(seed, receipt, description, [item], null);
		await using (var db = fixture.CreateDbContext())
		{
			var original = await db.CategoryAssistanceAttempts.AsNoTracking().SingleAsync(x => x.Id == seed.Attempt);
			var request = JsonSerializer.Deserialize<CategorySuggestionRequest>(original.RequestJson)!;
			var row = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == item);
			original.Id = id;
			original.RequestId = Guid.NewGuid();
			original.SourceHash = NormalizationCatalogPolicy.Hash(source);
			original.Location = source.Location;
			original.Description = source.Description;
			original.RequestJson = JsonSerializer.Serialize(request with { Description = source.Description });
			original.ReceiptId = receipt;
			original.ItemId = item;
			original.SourceRevision = original.ItemRevision = row.NormalizationRevision;
			original.ReceiptRevision = await db.Receipts.Where(x => x.Id == receipt).Select(x => x.NormalizationRevision).SingleAsync();
			original.AssignmentId = await db.CurrentClassifications.Where(x => x.ReceiptItemId == item).Select(x => x.AssignmentId).SingleAsync();
			original.CompletedAt = DateTimeOffset.UtcNow;
			db.CategoryAssistanceAttempts.Add(original);
			await db.SaveChangesAsync();
		}
		await ObserveAsync(seed, item, seed.Actor, description: description, attempt: id);
		return (id, item);
	}

	private CategoryAssistanceAutoWriter Writer(CategoryAutoWriteOptions? options = null,
		CategoryAssistanceAutoWriteCursor? cursor = null, IDbContextFactory<ApplicationDbContext>? factory = null)
	{
		var capability = new Mock<ICategoryAssistanceCapabilitySource>();
		capability.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CategoryAssistanceCapability(
			"jev", CategoryAssistanceCapabilityState.Ready, [CategorySuggestionPolicy.Model]));
		var monitor = new Mock<IOptionsMonitor<CategoryAutoWriteOptions>>();
		monitor.SetupGet(x => x.CurrentValue).Returns(options ?? new CategoryAutoWriteOptions());
		return new(factory ?? new Factory(fixture), capability.Object, Options.Create(new ClassificationOptions { WritesEnabled = true }),
			monitor.Object, TimeProvider.System, cursor ?? new CategoryAssistanceAutoWriteCursor());
	}

	private HistoricalReclassificationService Historical() =>
		new(new Factory(fixture), TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = true }));

	private ClassificationService Classification() =>
		new(new Factory(fixture), TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = true }));

	private static NormalizationSourceKey Normalized => NormalizationCatalogPolicy.Normalize(new(Store, Description, null));

	private async Task<PurchaseClassificationEntity> CurrentAsync(Guid item)
	{
		await using var db = fixture.CreateDbContext();
		return await (from current in db.CurrentClassifications.AsNoTracking()
					  join history in db.Classifications.AsNoTracking() on current.AssignmentId equals history.Id
					  where current.ReceiptItemId == item
					  select history).SingleAsync();
	}

	private async Task SetAutoApplyAsync(bool value)
	{
		// The flag alone; rotating the settings revision would fence every attempt instead.
		await using var db = fixture.CreateDbContext();
		await db.CategoryAssistanceSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.AutoApply, value));
	}

	private async Task<string> UserAsync()
	{
		string actor = "auto-write-" + Guid.NewGuid();
		await using var db = fixture.CreateDbContext();
		db.Users.Add(new ApplicationUser { Id = actor, UserName = actor, NormalizedUserName = actor.ToUpperInvariant() });
		var role = await db.Roles.SingleOrDefaultAsync(x => x.Name == AppRoles.Admin);
		if (role == null) { role = new IdentityRole(AppRoles.Admin) { NormalizedName = AppRoles.Admin.ToUpperInvariant() }; db.Roles.Add(role); }
		db.UserRoles.Add(new IdentityUserRole<string> { UserId = actor, RoleId = role.Id });
		await db.SaveChangesAsync();
		return actor;
	}

	/// <summary>
	/// A two-level tree (Food: Bread, Milk; Household), one receipt whose purchases carry an unresolved legacy
	/// assignment, a completed two-stage suggestion built by the production policy, and one save observation per purchase.
	/// </summary>
	private async Task<Seed> SeedAsync(double root, double leaf, int items = 1, bool leafUnderRoot = true,
		bool categorizeSecond = false, int pendingLimit = 3, bool portable = false)
	{
		string actor = await UserAsync();
		Guid taxonomy = Guid.NewGuid(), food = Guid.NewGuid(), bread = Guid.NewGuid(), milk = Guid.NewGuid(), household = Guid.NewGuid();
		Guid attemptId = Guid.NewGuid(), requestId = Guid.NewGuid(), receiptId = Guid.NewGuid();
		List<Guid> itemIds = [.. Enumerable.Range(0, items).Select(_ => Guid.NewGuid())];
		var source = Normalized;
		if (portable)
		{
			// Archive validation replays the legacy backfill and taxonomy history, so build them through the real services.
			var migration = new LegacyTaxonomyMigrationService(new Factory(fixture));
			var plan = await migration.PreflightAsync(new("auto-write-fixture", Guid.NewGuid(), actor, DateTimeOffset.UtcNow));
			await migration.ApplyAsync(LegacyMigrationFreeze.Source(plan));
			Guid head;
			await using (var db = fixture.CreateDbContext()) { head = (await db.TaxonomyHeads.SingleAsync()).RevisionId; }
			taxonomy = (await new TaxonomyService(new Factory(fixture), TimeProvider.System).ChangeAsync(actor, new(head, Guid.NewGuid(),
				"Auto-write test taxonomy",
				[new TaxonomyEdit("create", food, Name: "Food", Definition: "Food purchases"),
				 new TaxonomyEdit("create", bread, food, "Bread", "Bread purchases"),
				 new TaxonomyEdit("create", milk, food, "Milk", "Milk purchases"),
				 new TaxonomyEdit("create", household, Name: "Household", Definition: "Household purchases")]))).Id;
		}
		await using (var db = fixture.CreateDbContext())
		{
			if (!portable)
			{
				db.TaxonomyRevisions.Add(new TaxonomyRevisionEntity
				{
					Id = taxonomy,
					Sequence = 1,
					PreviousRevisionId = (await db.TaxonomyHeads.SingleAsync()).RevisionId,
					Actor = actor,
					Reason = "Auto-write test taxonomy",
					OperationId = Guid.NewGuid(),
					RecordedAt = DateTimeOffset.UtcNow
				});
				foreach (var (id, name, parent) in new[] { (food, "Food", (Guid?)null), (bread, "Bread", food), (milk, "Milk", food), (household, "Household", null) })
				{
					db.TaxonomyIdentities.Add(new TaxonomyIdentityEntity { Id = id });
					db.TaxonomyNodes.Add(new TaxonomyNodeEntity { RevisionId = taxonomy, CategoryId = id, Name = name, ParentId = parent, Definition = name + " purchases" });
				}
				(await db.TaxonomyHeads.SingleAsync()).RevisionId = taxonomy;
				db.LegacyMigrations.Add(new LegacyMigrationEntity
				{
					Actor = actor,
					OperationId = Guid.NewGuid(),
					OperationKind = LegacyTaxonomyMigrationService.InitialOperationKind,
					SourceDataset = "test",
					RequestHash = new string('B', 64),
					RevisionId = taxonomy,
					ObservedAt = DateTimeOffset.UtcNow
				});
			}
			var settings = await db.CategoryAssistanceSettings.SingleAsync();
			settings.Enabled = true; settings.Provider = "jev"; settings.Model = CategorySuggestionPolicy.Model;
			settings.PendingLimit = pendingLimit;
			var connection = await db.CategoryAssistanceConnection.SingleAsync();
			connection.Provider = "jev"; connection.Model = CategorySuggestionPolicy.Model;
			connection.ConfigurationFingerprint = new string('A', 64);
			connection.Generation = Guid.NewGuid(); connection.Changing = false;
			await db.SaveChangesAsync();

			var request = new CategorySuggestionRequest(source.Description, taxonomy.ToString("D"),
				[new(food.ToString("D"), "Food", "Food purchases", null), new(bread.ToString("D"), "Bread", "Bread purchases", food.ToString("D")),
				 new(milk.ToString("D"), "Milk", "Milk purchases", food.ToString("D")), new(household.ToString("D"), "Household", "Household purchases", null)],
				source.Location);
			var stage1 = CategorySuggestionPolicy.DecideStage1(request, new(CategorySuggestionPolicy.Model, food.ToString("D"), root,
				new Dictionary<string, double> { [food.ToString("D")] = root, [household.ToString("D")] = 1 - root }, 0.9, 5, 1), "jev").Suggestion!;
			string top = (leafUnderRoot ? bread : household).ToString("D");
			string[] rest = [.. new[] { bread, milk, household }.Select(x => x.ToString("D")).Where(x => x != top)];
			var final = CategorySuggestionPolicy.DecideStage2(request, stage1, new(CategorySuggestionPolicy.Model, top, leaf,
				new Dictionary<string, double> { [top] = leaf, [rest[0]] = (1 - leaf) / 2, [rest[1]] = (1 - leaf) / 2 }, null, 4, 1)).Suggestion!;
			final.LeafCandidates![0].CategoryId.Should().Be(top);
			db.CategoryAssistanceAttempts.Add(new CategoryAssistanceAttemptEntity
			{
				Id = attemptId,
				RequestId = requestId,
				RestoreGeneration = settings.RestoreGeneration,
				SourceHash = NormalizationCatalogPolicy.Hash(source),
				Location = source.Location,
				Description = source.Description,
				ItemCodeKey = "",
				TaxonomyRevision = taxonomy,
				Provider = "jev",
				Model = CategorySuggestionPolicy.Model,
				QuestionPolicyVersion = CategorySuggestionPolicy.Version,
				SettingsRevision = settings.Revision,
				ConnectionGeneration = connection.Generation,
				ReceiptId = receiptId,
				ItemId = itemIds[0],
				RequestJson = JsonSerializer.Serialize(request),
				State = "suggested",
				CreatedAt = DateTimeOffset.UtcNow,
				CompletedAt = DateTimeOffset.UtcNow,
				ResultJson = JsonSerializer.Serialize(new CategoryAssistanceCompletion(CategoryAssistanceAttemptOutcome.Suggested, final))
			});
			await db.SaveChangesAsync();
		}
		var seed = new Seed(actor, taxonomy, food, bread, household, attemptId, requestId, itemIds);
		await AddReceiptAsync(seed, receiptId, Description, itemIds, categorizeSecond ? household : null, portable);
		await using (var db = fixture.CreateDbContext())
		{
			// The attempt was built from the first purchase's revision and assignment.
			var first = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == itemIds[0]);
			var attempt = await db.CategoryAssistanceAttempts.SingleAsync(x => x.Id == attemptId);
			attempt.SourceRevision = attempt.ItemRevision = first.NormalizationRevision;
			attempt.ReceiptRevision = await db.Receipts.Where(x => x.Id == receiptId).Select(x => x.NormalizationRevision).SingleAsync();
			attempt.AssignmentId = await db.CurrentClassifications.Where(x => x.ReceiptItemId == itemIds[0]).Select(x => x.AssignmentId).SingleAsync();
			await db.SaveChangesAsync();
		}
		foreach (Guid item in itemIds) { await ObserveAsync(seed, item, actor); }
		return seed;
	}

	/// <summary>Adds a purchase on its own receipt and, unless detached, a later save of it attached to the seeded attempt.</summary>
	private async Task<Guid> AddItemAsync(Seed seed, string? actor = null, string description = Description, bool attach = true)
	{
		Guid item = Guid.NewGuid();
		await AddReceiptAsync(seed, Guid.NewGuid(), description, [item], null);
		await ObserveAsync(seed, item, actor ?? seed.Actor, attach, description);
		return item;
	}

	private async Task AddReceiptAsync(Seed seed, Guid receiptId, string description, IReadOnlyList<Guid> items, Guid? secondCategory,
		bool portable = false)
	{
		await using var db = fixture.CreateDbContext();
		db.Receipts.Add(new ReceiptEntity { Id = receiptId, Location = Store, Date = new DateOnly(2026, 10, 30), TaxAmountCurrency = Currency.USD });
		if (portable)
		{
			await db.SaveChangesAsync();
			// The receipt writer appends the unresolved legacy assignment, as a real save does.
			await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture, seed.Actor)).CreateAsync(
				[.. items.Select(item => new ReceiptItemEntity
				{
					Id = item, ReceiptId = receiptId, Description = description, Category = "",
					UnitPriceCurrency = Currency.USD, TotalAmountCurrency = Currency.USD
				})], default);
			return;
		}
		foreach (Guid item in items)
		{
			db.ReceiptItems.Add(new ReceiptItemEntity
			{
				Id = item,
				ReceiptId = receiptId,
				Description = description,
				Category = "",
				UnitPriceCurrency = Currency.USD,
				TotalAmountCurrency = Currency.USD
			});
		}
		await db.SaveChangesAsync();
		for (int i = 0; i < items.Count; i++)
		{
			Guid assignment = Guid.NewGuid();
			// Every purchase carries an assignment once the legacy backfill is complete; unresolved ones have no category.
			db.Classifications.Add(new PurchaseClassificationEntity
			{
				Id = assignment,
				ReceiptItemId = items[i],
				Sequence = 1,
				CategoryId = i == 1 ? secondCategory : null,
				TaxonomyRevisionId = seed.Taxonomy,
				RecordedAt = DateTimeOffset.UtcNow,
				Actor = seed.Actor,
				Source = "legacy_import",
				Reason = "Unresolved legacy observation",
				OperationId = Guid.NewGuid()
			});
			db.CurrentClassifications.Add(new CurrentClassificationEntity { ReceiptItemId = items[i], AssignmentId = assignment });
		}
		await db.SaveChangesAsync();
	}

	/// <summary>A post-consent, no-choice save of the purchase as it is now.</summary>
	private async Task ObserveAsync(Seed seed, Guid item, string actor, bool attach = true, string description = Description,
		Guid? attempt = null)
	{
		var source = NormalizationCatalogPolicy.Normalize(new(Store, description, null));
		await using var db = fixture.CreateDbContext();
		var settings = await db.CategoryAssistanceSettings.AsNoTracking().SingleAsync();
		var row = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == item);
		db.CategoryAssistanceObservations.Add(new CategoryAssistanceObservationEntity
		{
			AttemptId = attach ? attempt ?? seed.Attempt : null,
			SaveId = Guid.NewGuid(),
			ReceiptItemId = item,
			ReceiptId = row.ReceiptId,
			Actor = actor,
			Location = source.Location,
			Description = source.Description,
			SourceHash = NormalizationCatalogPolicy.Hash(source),
			ItemRevision = row.NormalizationRevision,
			ReceiptRevision = await db.Receipts.Where(x => x.Id == row.ReceiptId).Select(x => x.NormalizationRevision).SingleAsync(),
			AssignmentId = await db.CurrentClassifications.Where(x => x.ReceiptItemId == item).Select(x => (Guid?)x.AssignmentId).SingleOrDefaultAsync(),
			SettingsRevision = settings.Revision,
			RestoreGeneration = settings.RestoreGeneration,
			TaxonomyRevision = seed.Taxonomy,
			RecordedAt = DateTimeOffset.UtcNow
		});
		await db.SaveChangesAsync();
	}
}
