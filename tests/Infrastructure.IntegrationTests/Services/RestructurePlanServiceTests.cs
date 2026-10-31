using System.Diagnostics;
using Application.Normalization;
using Application.Taxonomy;
using Application.Taxonomy.Restructure;
using Common;
using Domain.Envelopes;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Envelopes;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed class RestructurePlanServiceTests(PostgresFixture server) : IsolatedPostgresTest(server)
{
	private const string Actor = "restructure-test-admin";
	private const string Budget = "budget-1";

	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }

	private static readonly IOptions<ClassificationOptions> Enabled = Options.Create(new ClassificationOptions { WritesEnabled = true });

	private RestructurePlanService Service()
	{
		Factory factory = new(fixture);
		return new(factory, TimeProvider.System, Enabled, new HistoricalReclassificationService(factory, TimeProvider.System, Enabled));
	}

	private TaxonomyService Tree() => new(new Factory(fixture), TimeProvider.System);

	/// <summary>Groceries {Dairy, N/A}, Event {Dairy} (an envelope root), Household; envelopes with YNAB links.</summary>
	private sealed record Seed(Guid Revision, Guid Groceries, Guid Dairy, Guid Placeholder, Guid Event, Guid EventDairy, Guid Household,
		Guid Pantry, Guid PantryDuplicate, Guid Party);

	private async Task<Seed> SeedAsync()
	{
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			db.Users.Add(new ApplicationUser { Id = Actor, UserName = Actor });
			IdentityRole role = new() { Id = Guid.NewGuid().ToString(), Name = AppRoles.Admin };
			db.Roles.Add(role);
			db.UserRoles.Add(new() { UserId = Actor, RoleId = role.Id });
			await db.SaveChangesAsync();
		}
		// The real initial backfill on an empty database marks classification as ready.
		LegacyTaxonomyMigrationService migration = new(new Factory(fixture));
		LegacyMigrationPlan plan = await migration.PreflightAsync(new("restructure-fixture", Guid.NewGuid(), Actor, DateTimeOffset.UtcNow));
		await migration.ApplyAsync(LegacyMigrationFreeze.Source(plan));

		Guid groceries = Guid.NewGuid(), dairy = Guid.NewGuid(), placeholder = Guid.NewGuid(), @event = Guid.NewGuid(),
			eventDairy = Guid.NewGuid(), household = Guid.NewGuid();
		TaxonomyRevision head = (await Tree().ReadAsync(null))!;
		TaxonomyRevision revision = await Tree().ChangeAsync(Actor, new(head.Id, Guid.NewGuid(), "Legacy tree",
		[
			// Archive the application's seed categories so the fixture tree is the whole active tree.
			.. head.Nodes.Where(node => node.ParentId == null && !node.Archived).Select(node => new Domain.Taxonomy.TaxonomyEdit("archive", node.Id)),
			// The genesis import may already hold seed categories, so the fixture's roots carry a suffix.
			new("create", groceries, Name: "Groceries (t)"), new("create", dairy, groceries, "Dairy"), new("create", placeholder, groceries, "N/A"),
			new("create", @event, Name: "Event (t)"), new("create", eventDairy, @event, "Dairy"), new("create", household, Name: "Household (t)"),
		]));

		Guid pantry = Guid.NewGuid(), duplicate = Guid.NewGuid(), party = Guid.NewGuid();
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			db.Envelopes.AddRange(
				new EnvelopeEntity { Id = pantry, Name = "Pantry", IsActive = true },
				new EnvelopeEntity { Id = duplicate, Name = "Pantry (2)", IsActive = false },
				new EnvelopeEntity { Id = party, Name = "Party", IsActive = true });
			DateTimeOffset now = DateTimeOffset.UtcNow;
			db.YnabEnvelopeMappings.AddRange(
				new YnabEnvelopeMappingEntity { Id = Guid.NewGuid(), EnvelopeId = pantry, YnabBudgetId = Budget, YnabCategoryId = "ynab-pantry", YnabCategoryName = "Pantry", YnabCategoryGroupName = "Food", CreatedAt = now, UpdatedAt = now },
				new YnabEnvelopeMappingEntity { Id = Guid.NewGuid(), EnvelopeId = party, YnabBudgetId = Budget, YnabCategoryId = "ynab-party", YnabCategoryName = "Party", YnabCategoryGroupName = "Fun", CreatedAt = now, UpdatedAt = now });
			db.ItemTypeEnvelopeDefaults.AddRange(
				new ItemTypeEnvelopeDefaultEntity { Id = Guid.NewGuid(), CategoryId = groceries, EnvelopeId = pantry, CreatedAt = now, UpdatedAt = now },
				new ItemTypeEnvelopeDefaultEntity { Id = Guid.NewGuid(), CategoryId = @event, EnvelopeId = party, CreatedAt = now, UpdatedAt = now });
			await db.SaveChangesAsync();
		}
		return new(revision.Id, groceries, dairy, placeholder, @event, eventDairy, household, pantry, duplicate, party);
	}

	/// <summary>Adds purchases already classified (as a legacy import) under one category, with a recorded envelope.</summary>
	private async Task<Guid[]> AddItemsAsync(Seed seed, Guid category, int count, Guid? envelope, string source = "legacy_import",
		string description = "WHOLE MILK")
	{
		Guid[] ids = new Guid[count];
		await using ApplicationDbContext db = fixture.CreateDbContext();
		// Ten purchases per receipt, about what a real receipt holds.
		for (int offset = 0; offset < count; offset += 10)
		{
			ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
			receipt.Location = "Corner Store";
			db.Receipts.Add(receipt);
			for (int index = offset; index < Math.Min(count, offset + 10); index++)
			{
				ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
				item.Description = description;
				item.EnvelopeId = envelope;
				item.EnvelopeSource = EnvelopeSource.Historical;
				db.ReceiptItems.Add(item);
				ids[index] = item.Id;
			}
			if (offset % 2000 == 0 || offset + 10 >= count)
			{
				await db.SaveChangesAsync();
				db.ChangeTracker.Clear();
			}
		}
		await db.Database.ExecuteSqlInterpolatedAsync($"""
			WITH items AS (SELECT unnest({ids}::uuid[]) AS id), written AS (
				INSERT INTO taxonomy."Assignments" ("Id", "ReceiptItemId", "Sequence", "PreviousAssignmentId", "CategoryId",
					"TaxonomyRevisionId", "RecordedAt", "Actor", "Source", "Reason", "OperationId")
				SELECT gen_random_uuid(), id, 1, NULL, {category}, {seed.Revision}, now(), {Actor}, {source}, 'seed', gen_random_uuid()
				FROM items RETURNING "Id", "ReceiptItemId")
			INSERT INTO taxonomy."CurrentAssignments" ("ReceiptItemId", "AssignmentId") SELECT "ReceiptItemId", "Id" FROM written
			""");
		return ids;
	}

	private static RestructureCategoryChange Define(Guid id, string text) => new("define", id, Definition: text);

	private static RestructurePlan Plan(Seed seed, DateTimeOffset snapshot, IReadOnlyList<RestructureItemReassignment>? items = null,
		Guid? planId = null) => new(RestructurePlan.FormatName, 1, planId ?? Guid.NewGuid(), "Item types, not envelopes", seed.Revision, snapshot,
		[
			Define(seed.Groceries, "Includes food and drink. Not cleaning products."),
			Define(seed.Dairy, "Includes milk, cheese and yogurt. Not plant milks."),
			Define(seed.Household, "Includes cleaning and paper products. Not food."),
			new("archive", seed.Event),
			new("archive", seed.Placeholder),
		],
		[new(seed.Event, Clear: true), new(seed.EventDairy, seed.Dairy, Occasion: "Event"), new(seed.Placeholder, Clear: true)],
		items, ["Event"], null, [new(new("Pantry (2)"), new("Pantry"))]);

	private async Task<RestructureApplicationSummary> ApplyAllAsync(Guid id)
	{
		RestructureApplicationSummary summary = await Service().ReadAsync(id);
		while (summary.Progress.Pending > 0) { summary = await Service().ApplyChunkAsync(Actor, id, 100); }
		if (summary.State == HistoricalOperationState.Preview) { summary = await Service().ApplyChunkAsync(Actor, id, 100); }
		return summary;
	}

	private async Task<RestructureApplicationSummary> UndoAllAsync(Guid id)
	{
		RestructureApplicationSummary summary;
		do { summary = await Service().UndoChunkAsync(Actor, id, 100); }
		while (summary.State != HistoricalOperationState.Undone);
		return summary;
	}

	private async Task<Dictionary<Guid, (Guid? Category, string Source, Guid? Envelope, EnvelopeSource EnvelopeSource, Guid? Occasion)>> SnapshotAsync()
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		return await (from item in db.ReceiptItems.AsNoTracking().IgnoreAutoIncludes()
					  join current in db.CurrentClassifications on item.Id equals current.ReceiptItemId
					  join assignment in db.Classifications on current.AssignmentId equals assignment.Id
					  select new { item.Id, assignment.CategoryId, assignment.Source, item.EnvelopeId, item.EnvelopeSource, item.OccasionId })
			.ToDictionaryAsync(x => x.Id, x => (x.CategoryId, x.Source, x.EnvelopeId, x.EnvelopeSource, x.OccasionId));
	}

	private async Task<List<Domain.Taxonomy.TaxonomyNode>> CurrentTreeAsync() => [.. (await Tree().ReadAsync(null))!.Nodes];

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

	[Fact]
	[Trait("Scale", "Restructure")]
	public async Task TwentyThousandPurchasesAreCheckedAndCapturedInOneApplyWithinBudget()
	{
		Seed seed = await SeedAsync();
		await AddItemsAsync(seed, seed.Placeholder, 10_000, seed.Pantry);
		await AddItemsAsync(seed, seed.EventDairy, 10_000, seed.Party);
		RestructurePlan plan = Plan(seed, DateTimeOffset.UtcNow);
		long memoryBefore = GC.GetTotalMemory(true);
		Stopwatch clock = Stopwatch.StartNew();

		RestructurePreview preview = await Service().PreviewAsync(plan);
		TimeSpan dryRun = clock.Elapsed;
		RestructureApplicationSummary begun = await Service().BeginAsync(Actor, Guid.NewGuid(), plan);
		TimeSpan capture = clock.Elapsed - dryRun;
		long memory = GC.GetTotalMemory(true) - memoryBefore;
		RestructureApplicationSummary chunk = begun;
		Stopwatch applying = Stopwatch.StartNew();
		for (int index = 0; index < 5; index++) { chunk = await Service().ApplyChunkAsync(Actor, begun.Id, 100); }
		TimeSpan perPurchase = applying.Elapsed / 500;
		await Service().CancelAsync(Actor, begun.Id);
		RestructureApplicationSummary undone = await UndoAllAsync(begun.Id);

		preview.Report.Items.Reassigned.Should().Be(20_000);
		begun.Progress.Pending.Should().Be(20_000);
		chunk.Progress.Applied.Should().Be(500);
		undone.Progress.Undone.Should().Be(500);
		var after = await SnapshotAsync();
		// The 500 put back carry a `restructure` undo assignment on their original category; nothing else changed.
		after.Values.Where(x => x.Source == "restructure").Should().HaveCount(500)
			.And.OnlyContain(x => x.Category == seed.Placeholder || x.Category == seed.EventDairy);
		// Generous bounds for a loaded host; the rehearsal measures a full apply on real data.
		dryRun.Should().BeLessThan(TimeSpan.FromMinutes(1));
		capture.Should().BeLessThan(TimeSpan.FromMinutes(2));
		perPurchase.Should().BeLessThan(TimeSpan.FromMilliseconds(250));
		memory.Should().BeLessThan(256L * 1024 * 1024);
		Console.WriteLine($"restructure scale: dry run {dryRun}, capture {capture}, {perPurchase.TotalMilliseconds:F1} ms per purchase, {memory / 1024 / 1024} MiB");
	}
}
