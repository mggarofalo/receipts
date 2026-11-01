using Application.Normalization;
using Application.Taxonomy;
using Application.Taxonomy.Restructure;
using Common;
using Domain.Envelopes;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Envelopes;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class RestructurePlanServiceTests(PostgresFixture server) : RestructureTestBase(server)
{
	[Fact]
	public async Task DryRunEqualsApplyAndUndoPutsEverythingBack()
	{
		// Arrange
		Seed seed = await SeedAsync();
		Guid[] eventDairy = await AddItemsAsync(seed, seed.EventDairy, 3, seed.Party);
		Guid[] placeholder = await AddItemsAsync(seed, seed.Placeholder, 2, seed.PantryDuplicate, description: "SPONGE");
		Guid[] dairy = await AddItemsAsync(seed, seed.Dairy, 2, seed.Pantry);
		DateTimeOffset snapshot = DateTimeOffset.UtcNow;
		RestructurePlan plan = Plan(seed, snapshot, [new(placeholder[0], seed.Household, Rule: "d")]);
		var before = await SnapshotAsync();
		var treeBefore = await CurrentTreeAsync();

		// Act
		RestructurePreview preview = await Service().PreviewAsync(plan);
		Guid id = Guid.NewGuid();
		RestructureApplicationSummary begun = await Service().BeginAsync(Actor, id, plan);
		RestructureApplicationSummary applied = await ApplyAllAsync(id);

		// Assert: the dry run is exactly what happened.
		preview.Valid.Should().BeTrue(string.Join(" ", preview.Errors));
		begun.Report.Should().BeEquivalentTo(preview.Report);
		applied.State.Should().Be(HistoricalOperationState.Completed);
		applied.Progress.Applied.Should().Be(preview.Report.Items.Reassigned).And.Be(5);
		var after = await SnapshotAsync();
		eventDairy.Should().AllSatisfy(item => after[item].Should().Match<(Guid? Category, string Source, Guid? Envelope, EnvelopeSource EnvelopeSource, Guid? Occasion)>(
			x => x.Category == seed.Dairy && x.Source == "restructure" && x.Envelope == seed.Party && x.Occasion != null));
		after[placeholder[0]].Category.Should().Be(seed.Household);
		after[placeholder[1]].Category.Should().BeNull();
		dairy.Should().AllSatisfy(item => after[item].Should().Be(before[item]));
		// Budget parity: only the envelope merge moves purchases between envelopes.
		after.Where(pair => before[pair.Key].Envelope != pair.Value.Envelope).Select(pair => pair.Key)
			.Should().BeEquivalentTo(placeholder);
		preview.Report.Budget.ItemsChangingEnvelope.Should().Be(2);
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			(await db.ItemTypeEnvelopeDefaults.AnyAsync(x => x.CategoryId == seed.Event)).Should().BeFalse();
			// The envelope root maps to "clear", so its default is dropped rather than moved.
			(await db.ItemTypeEnvelopeDefaults.AnyAsync(x => x.CategoryId == seed.Dairy)).Should().BeFalse();
			(await db.Occasions.SingleAsync()).Name.Should().Be("Event");
			string reason = await db.Classifications.Where(x => x.ReceiptItemId == placeholder[0] && x.Source == "restructure").Select(x => x.Reason).SingleAsync();
			reason.Should().Contain("\"Rule\":\"d\"").And.Contain(plan.PlanId.ToString());
		}

		// Act: undo the whole application.
		RestructureApplicationSummary undone = await UndoAllAsync(id);

		// Assert: categories, purchases, envelopes, defaults and occasions are as before.
		undone.Progress.Undone.Should().Be(5);
		undone.UndoneAt.Should().NotBeNull();
		var restored = await SnapshotAsync();
		restored.Should().HaveCount(before.Count);
		foreach ((Guid item, var value) in before)
		{
			restored[item].Category.Should().Be(value.Category);
			restored[item].Envelope.Should().Be(value.Envelope);
			restored[item].Occasion.Should().Be(value.Occasion);
		}
		var treeAfter = await CurrentTreeAsync();
		treeAfter.Should().BeEquivalentTo(treeBefore);
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			(await db.ItemTypeEnvelopeDefaults.Select(x => new { x.CategoryId, x.EnvelopeId }).ToListAsync())
				.Should().BeEquivalentTo([new { CategoryId = seed.Groceries, EnvelopeId = seed.Pantry }, new { CategoryId = seed.Event, EnvelopeId = seed.Party }]);
			(await db.Occasions.AnyAsync()).Should().BeFalse();
		}
	}

	[Fact]
	public async Task TheSamePlanIsRefusedWhileAppliedReplaysForTheSameApplicationAndAppliesAgainAfterUndo()
	{
		Seed seed = await SeedAsync();
		await AddItemsAsync(seed, seed.Placeholder, 1, seed.Pantry);
		RestructurePlan plan = Plan(seed, DateTimeOffset.UtcNow);
		Guid id = Guid.NewGuid();
		RestructureApplicationSummary first = await Service().BeginAsync(Actor, id, plan);

		(await Service().BeginAsync(Actor, id, plan)).Id.Should().Be(first.Id);
		Func<Task> again = () => Service().BeginAsync(Actor, Guid.NewGuid(), plan);
		(await again.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		Func<Task> changed = () => Service().BeginAsync(Actor, id, plan with { Reason = "Different" });
		(await changed.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);

		await ApplyAllAsync(id);
		await UndoAllAsync(id);
		// The undone tree equals the plan's base again, though under a newer revision.
		RestructureApplicationSummary second = await Service().BeginAsync(Actor, Guid.NewGuid(), plan);
		(await ApplyAllAsync(second.Id)).Progress.Applied.Should().Be(1);
	}

	[Fact]
	public async Task LaterDecisionsAreConflictsWhileLaterPurchasesFollowTheirNodeMapping()
	{
		Seed seed = await SeedAsync();
		DateTimeOffset snapshot = DateTimeOffset.UtcNow;
		await Task.Delay(20);
		Guid[] savedLater = await AddItemsAsync(seed, seed.EventDairy, 2, seed.Party, source: "legacy_write");
		Guid[] decidedLater = await AddItemsAsync(seed, seed.EventDairy, 1, seed.Party, source: "manual");

		RestructureApplicationSummary summary = await ApplyAllAsync((await Service().BeginAsync(Actor, Guid.NewGuid(), Plan(seed, snapshot))).Id);

		summary.Progress.Applied.Should().Be(2);
		summary.Progress.Conflicted.Should().Be(1);
		var after = await SnapshotAsync();
		savedLater.Should().AllSatisfy(item => after[item].Category.Should().Be(seed.Dairy));
		after[decidedLater[0]].Category.Should().Be(seed.EventDairy);
		after[decidedLater[0]].Source.Should().Be("manual");
		RestructureItemPage conflicts = await Service().ReadItemsAsync(summary.Id, RestructureItemFilter.Conflicted, 0, 100);
		conflicts.Items.Should().ContainSingle().Which.Should().Match<RestructureItemRow>(row =>
			row.ItemId == decidedLater[0] && row.FromPath == "Event (t) / Dairy" && row.Reason == "classified-after-plan");
	}

	[Fact]
	public async Task AnInvalidPlanChangesNothing()
	{
		Seed seed = await SeedAsync();
		await AddItemsAsync(seed, seed.Placeholder, 1, seed.Pantry);
		RestructurePlan plan = Plan(seed, DateTimeOffset.UtcNow) with { NodeMappings = [new(seed.Event, Clear: true)] };
		Guid head = (await Tree().ReadAsync(null))!.Id;

		RestructurePreview preview = await Service().PreviewAsync(plan);
		Func<Task> begin = () => Service().BeginAsync(Actor, Guid.NewGuid(), plan);

		preview.Valid.Should().BeFalse();
		TaxonomyException error = (await begin.Should().ThrowAsync<TaxonomyException>()).Which;
		error.Status.Should().Be(400);
		error.Extensions["errors"].Should().BeEquivalentTo(preview.Errors);
		(await Tree().ReadAsync(null))!.Id.Should().Be(head);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		(await db.RestructureApplications.AnyAsync()).Should().BeFalse();
		(await db.Occasions.AnyAsync()).Should().BeFalse();
	}

	[Fact]
	public async Task UndoRefusesWhenCategoriesChangedAfterTheRestructure()
	{
		Seed seed = await SeedAsync();
		await AddItemsAsync(seed, seed.Placeholder, 1, seed.Pantry);
		RestructureApplicationSummary summary = await ApplyAllAsync((await Service().BeginAsync(Actor, Guid.NewGuid(), Plan(seed, DateTimeOffset.UtcNow))).Id);
		TaxonomyRevision head = (await Tree().ReadAsync(null))!;
		await Tree().ChangeAsync(Actor, new(head.Id, Guid.NewGuid(), "Later edit", [new("rename", seed.Household, Name: "Home")]));

		Func<Task> undo = () => Service().UndoChunkAsync(Actor, summary.Id, 100);

		(await undo.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		(await Service().ReadAsync(summary.Id)).State.Should().Be(HistoricalOperationState.Completed);
	}

	[Fact]
	public async Task ApprovedMappingsFollowTheirCategoryAndComeBackOnUndo()
	{
		Seed seed = await SeedAsync();
		NormalizationSourceKey source = NormalizationCatalogPolicy.Normalize(new("Corner Store", "PARTY MILK", null));
		string fingerprint = NormalizationCatalogPolicy.Hash(source);
		CategoryMappingService mappings = new(new Factory(fixture), TimeProvider.System);
		await mappings.ChangeAsync(Actor, new(source, fingerprint, null, seed.Revision, seed.EventDairy, Guid.NewGuid(), "Remember"));
		RestructureApplicationSummary summary = await Service().BeginAsync(Actor, Guid.NewGuid(), Plan(seed, DateTimeOffset.UtcNow));

		CategoryMappingResolution moved = await mappings.ResolveAsync(source, fingerprint);
		await ApplyAllAsync(summary.Id);
		await UndoAllAsync(summary.Id);
		CategoryMappingResolution back = await mappings.ResolveAsync(source, fingerprint);

		summary.Report.Mappings.Repointed.Should().Be(1);
		moved.Should().Match<CategoryMappingResolution>(r => r.Disposition == "approved" && r.CategoryId == seed.Dairy);
		back.Should().Match<CategoryMappingResolution>(r => r.Disposition == "approved" && r.CategoryId == seed.EventDairy);
	}

	private async Task AddAdminAsync(string id)
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		db.Users.Add(new ApplicationUser { Id = id, UserName = id });
		db.UserRoles.Add(new() { UserId = id, RoleId = await db.Roles.Where(x => x.Name == AppRoles.Admin).Select(x => x.Id).SingleAsync() });
		await db.SaveChangesAsync();
	}

	private async Task<TaxonomyRevision> RenameAsync(Guid category, string name)
	{
		TaxonomyRevision head = (await Tree().ReadAsync(null))!;
		return await Tree().ChangeAsync(Actor, new(head.Id, Guid.NewGuid(), "Later edit", [new("rename", category, Name: name)]));
	}

	[Fact]
	public async Task CategoryEditsAreRefusedWhileARestructureIsUnfinishedAndAllowedOnceItCompletes()
	{
		// Arrange
		Seed seed = await SeedAsync();
		await AddItemsAsync(seed, seed.Placeholder, 1, seed.Pantry);
		RestructureApplicationSummary begun = await Service().BeginAsync(Actor, Guid.NewGuid(), Plan(seed, DateTimeOffset.UtcNow));

		// Act
		Func<Task> midway = () => RenameAsync(seed.Household, "Home");

		// Assert
		begun.State.Should().Be(HistoricalOperationState.Preview);
		(await midway.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		(await ApplyAllAsync(begun.Id)).State.Should().Be(HistoricalOperationState.Completed);
		(await RenameAsync(seed.Household, "Home")).Nodes.Should().Contain(node => node.Id == seed.Household && node.Name == "Home");
	}

	[Fact]
	public async Task AnotherEnabledAdministratorCanContinueCancelAndUndoARestructure()
	{
		// Arrange
		const string other = "restructure-test-other-admin";
		Seed seed = await SeedAsync();
		await AddAdminAsync(other);
		await AddItemsAsync(seed, seed.Placeholder, 3, seed.Pantry);
		RestructureApplicationSummary begun = await Service().BeginAsync(Actor, Guid.NewGuid(), Plan(seed, DateTimeOffset.UtcNow));

		// Act
		RestructureApplicationSummary chunk = await Service().ApplyChunkAsync(other, begun.Id, 1);
		RestructureApplicationSummary cancelled = await Service().CancelAsync(other, begun.Id);
		RestructureApplicationSummary undone;
		do { undone = await Service().UndoChunkAsync(other, begun.Id, 100); }
		while (undone.State != HistoricalOperationState.Undone);

		// Assert
		chunk.Progress.Applied.Should().Be(1);
		cancelled.State.Should().Be(HistoricalOperationState.Cancelled);
		undone.Progress.Undone.Should().Be(1);
		undone.UndoneAt.Should().NotBeNull();
	}

	[Fact]
	public async Task APlanThatCreatesACategoryAppliesAgainAfterItWasUndone()
	{
		// Arrange
		Seed seed = await SeedAsync();
		Guid[] placeholder = await AddItemsAsync(seed, seed.Placeholder, 1, seed.Pantry);
		Guid created = Guid.NewGuid();
		RestructurePlan basePlan = Plan(seed, DateTimeOffset.UtcNow, [new(placeholder[0], created, Rule: "d")]);
		RestructurePlan plan = basePlan with
		{
			TaxonomyChanges = [.. basePlan.TaxonomyChanges, new("create", created, seed.Groceries, "Pantry staples", "Includes flour and rice. Not snacks.")],
		};
		Guid first = (await Service().BeginAsync(Actor, Guid.NewGuid(), plan)).Id;
		await ApplyAllAsync(first);
		await UndoAllAsync(first);
		(await CurrentTreeAsync()).Single(node => node.Id == created).Archived.Should().BeTrue();

		// Act
		RestructurePreview preview = await Service().PreviewAsync(plan);
		RestructureApplicationSummary again = await ApplyAllAsync((await Service().BeginAsync(Actor, Guid.NewGuid(), plan)).Id);

		// Assert
		preview.Valid.Should().BeTrue(string.Join(" ", preview.Errors));
		again.State.Should().Be(HistoricalOperationState.Completed);
		again.Progress.Applied.Should().Be(1);
		(await CurrentTreeAsync()).Single(node => node.Id == created).Should().Be(
			new Domain.Taxonomy.TaxonomyNode(created, seed.Groceries, "Pantry staples", "Includes flour and rice. Not snacks.", false));
		(await SnapshotAsync())[placeholder[0]].Category.Should().Be(created);
	}

	[Fact]
	public async Task UndoComparesCategoryContentSoAnEditThatWasPutBackDoesNotBlockIt()
	{
		// Arrange
		Seed seed = await SeedAsync();
		await AddItemsAsync(seed, seed.Placeholder, 1, seed.Pantry);
		RestructureApplicationSummary summary = await ApplyAllAsync((await Service().BeginAsync(Actor, Guid.NewGuid(), Plan(seed, DateTimeOffset.UtcNow))).Id);
		await RenameAsync(seed.Household, "Home");

		// Act
		Func<Task> whileEdited = () => Service().UndoChunkAsync(Actor, summary.Id, 100);
		TaxonomyException refused = (await whileEdited.Should().ThrowAsync<TaxonomyException>()).Which;
		await RenameAsync(seed.Household, "Household (t)");
		RestructureApplicationSummary undone = await UndoAllAsync(summary.Id);

		// Assert
		refused.Status.Should().Be(409);
		undone.Progress.Undone.Should().Be(1);
		undone.UndoneAt.Should().NotBeNull();
	}

	[Fact]
	public async Task AnOccasionTheUserChangedAfterCaptureIsLeftByApplyAndUndo()
	{
		// Arrange
		Seed seed = await SeedAsync();
		Guid[] items = await AddItemsAsync(seed, seed.EventDairy, 2, seed.Party);
		RestructureApplicationSummary begun = await Service().BeginAsync(Actor, Guid.NewGuid(), Plan(seed, DateTimeOffset.UtcNow));
		Guid birthday = Guid.NewGuid();
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			db.Occasions.Add(new OccasionEntity { Id = birthday, Name = "Birthday", IsActive = true });
			await db.SaveChangesAsync();
			await db.ReceiptItems.Where(x => x.Id == items[0]).ExecuteUpdateAsync(x => x.SetProperty(i => i.OccasionId, birthday));
		}

		// Act
		await ApplyAllAsync(begun.Id);
		var applied = await SnapshotAsync();
		await UndoAllAsync(begun.Id);
		var undone = await SnapshotAsync();

		// Assert: the purchase still moves, but keeps the user's occasion throughout.
		applied[items[0]].Category.Should().Be(seed.Dairy);
		applied[items[0]].Occasion.Should().Be(birthday);
		applied[items[1]].Occasion.Should().NotBeNull().And.NotBe(birthday);
		undone[items[0]].Category.Should().Be(seed.EventDairy);
		undone[items[0]].Occasion.Should().Be(birthday);
		undone[items[1]].Occasion.Should().BeNull();
	}

	[Fact]
	public async Task DryRunMergeCountsIncludeDeletedPurchasesTheMergeMoves()
	{
		// Arrange
		Seed seed = await SeedAsync();
		Guid[] duplicates = await AddItemsAsync(seed, seed.Dairy, 3, seed.PantryDuplicate);
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			await db.ReceiptItems.IgnoreQueryFilters().Where(x => x.Id == duplicates[0])
				.ExecuteUpdateAsync(x => x.SetProperty(i => i.DeletedAt, DateTimeOffset.UtcNow));
		}
		RestructurePlan plan = Plan(seed, DateTimeOffset.UtcNow);

		// Act
		RestructurePreview preview = await Service().PreviewAsync(plan);
		await Service().BeginAsync(Actor, Guid.NewGuid(), plan);

		// Assert
		await using ApplicationDbContext after = fixture.CreateDbContext();
		int moved = await after.ReceiptItems.IgnoreQueryFilters().CountAsync(x => duplicates.Contains(x.Id) && x.EnvelopeId == seed.Pantry);
		moved.Should().Be(3);
		preview.Report.EnvelopeMerges.Should().ContainSingle().Which.Items.Should().Be(moved);
		preview.Report.Budget.ItemsChangingEnvelope.Should().Be(moved);
	}

	[Fact]
	public async Task APlanContainingNulIsRefusedAndWritesNothing()
	{
		// Arrange
		Seed seed = await SeedAsync();
		Guid[] placeholder = await AddItemsAsync(seed, seed.Placeholder, 1, seed.Pantry);
		RestructurePlan plan = Plan(seed, DateTimeOffset.UtcNow,
			[new(placeholder[0], seed.Household, Rule: "d", Evidence: System.Text.Json.JsonSerializer.SerializeToElement(new { note = "a\0b" }))]);
		Guid head = (await Tree().ReadAsync(null))!.Id;

		// Act
		Func<Task> begin = () => Service().BeginAsync(Actor, Guid.NewGuid(), plan);

		// Assert
		TaxonomyException error = (await begin.Should().ThrowAsync<TaxonomyException>()).Which;
		error.Status.Should().Be(400);
		error.Message.Should().Contain("NUL");
		(await Tree().ReadAsync(null))!.Id.Should().Be(head);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		(await db.RestructureApplications.AnyAsync()).Should().BeFalse();
		(await db.HistoricalOperations.AnyAsync(x => x.Kind == Infrastructure.Entities.Taxonomy.HistoricalOperationKind.RestructureV1)).Should().BeFalse();
		(await db.Occasions.AnyAsync()).Should().BeFalse();
	}
}
