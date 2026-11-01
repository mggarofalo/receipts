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

// The restructure harness: a seeded legacy tree with envelopes, bulk purchases and plan helpers,
// shared by the behaviour tests and the isolated scale test.
public abstract class RestructureTestBase(PostgresFixture server) : IsolatedPostgresTest(server)
{
	protected const string Actor = "restructure-test-admin";
	protected const string Budget = "budget-1";

	protected sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }

	protected static readonly IOptions<ClassificationOptions> Enabled = Options.Create(new ClassificationOptions { WritesEnabled = true });

	protected RestructurePlanService Service()
	{
		Factory factory = new(fixture);
		return new(factory, TimeProvider.System, Enabled, new HistoricalReclassificationService(factory, TimeProvider.System, Enabled));
	}

	protected TaxonomyService Tree() => new(new Factory(fixture), TimeProvider.System);

	/// <summary>Groceries {Dairy, N/A}, Event {Dairy} (an envelope root), Household; envelopes with YNAB links.</summary>
	protected sealed record Seed(Guid Revision, Guid Groceries, Guid Dairy, Guid Placeholder, Guid Event, Guid EventDairy, Guid Household,
		Guid Pantry, Guid PantryDuplicate, Guid Party);

	protected async Task<Seed> SeedAsync()
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
	protected async Task<Guid[]> AddItemsAsync(Seed seed, Guid category, int count, Guid? envelope, string source = "legacy_import",
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

	protected static RestructureCategoryChange Define(Guid id, string text) => new("define", id, Definition: text);

	protected static RestructurePlan Plan(Seed seed, DateTimeOffset snapshot, IReadOnlyList<RestructureItemReassignment>? items = null,
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

	protected async Task<RestructureApplicationSummary> ApplyAllAsync(Guid id)
	{
		RestructureApplicationSummary summary = await Service().ReadAsync(id);
		while (summary.Progress.Pending > 0) { summary = await Service().ApplyChunkAsync(Actor, id, 100); }
		if (summary.State == HistoricalOperationState.Preview) { summary = await Service().ApplyChunkAsync(Actor, id, 100); }
		return summary;
	}

	protected async Task<RestructureApplicationSummary> UndoAllAsync(Guid id)
	{
		RestructureApplicationSummary summary;
		do { summary = await Service().UndoChunkAsync(Actor, id, 100); }
		while (summary.State != HistoricalOperationState.Undone);
		return summary;
	}

	protected async Task<Dictionary<Guid, (Guid? Category, string Source, Guid? Envelope, EnvelopeSource EnvelopeSource, Guid? Occasion)>> SnapshotAsync()
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		return await (from item in db.ReceiptItems.AsNoTracking().IgnoreAutoIncludes()
					  join current in db.CurrentClassifications on item.Id equals current.ReceiptItemId
					  join assignment in db.Classifications on current.AssignmentId equals assignment.Id
					  select new { item.Id, assignment.CategoryId, assignment.Source, item.EnvelopeId, item.EnvelopeSource, item.OccasionId })
			.ToDictionaryAsync(x => x.Id, x => (x.CategoryId, x.Source, x.EnvelopeId, x.EnvelopeSource, x.OccasionId));
	}

	protected async Task<List<Domain.Taxonomy.TaxonomyNode>> CurrentTreeAsync() => [.. (await Tree().ReadAsync(null))!.Nodes];
}
