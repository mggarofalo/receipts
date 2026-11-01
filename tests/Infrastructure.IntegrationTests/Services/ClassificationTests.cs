using Application.Taxonomy;
using Common;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public sealed partial class ClassificationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
	private const string Actor = "classification-test-admin";
	private sealed class Factory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext(); }
	private ClassificationService Service(bool enabled = true) => new(new Factory(fixture), TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = enabled }));
	private TaxonomyService Tree() => new(new Factory(fixture), TimeProvider.System);
	private async Task<(Guid Item, Guid Revision, Guid Root, Guid Child, Guid Assignment)> SeedAsync()
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		if (!await db.Users.AnyAsync(x => x.Id == Actor))
		{
			db.Users.Add(new ApplicationUser { Id = Actor, UserName = Actor });
			IdentityRole? role = await db.Roles.FirstOrDefaultAsync(x => x.Name == AppRoles.Admin);
			if (role == null) { role = new() { Id = Guid.NewGuid().ToString(), Name = AppRoles.Admin }; db.Roles.Add(role); }
			db.UserRoles.Add(new() { UserId = Actor, RoleId = role.Id });
		}
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate(); receipt.Date = new(1999, 1, 1);
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		db.Receipts.Add(receipt); await db.SaveChangesAsync();
		// Run the real initial backfill before creating an administrator taxonomy.
		// Subsequent test items go through the compatibility writer, avoiding readiness gaps.
		var migration = new LegacyTaxonomyMigrationService(new Factory(fixture));
		if (!await db.LegacyMigrations.AnyAsync())
		{
			db.ReceiptItems.Add(item); await db.SaveChangesAsync();
			var plan = await migration.PreflightAsync(new("classification-fixture", Guid.NewGuid(), Actor, DateTimeOffset.UtcNow));
			await migration.ApplyAsync(LegacyMigrationFreeze.Source(plan));
		}
		else
		{
			await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync([item], default);
		}
		Guid assignment = (await Service().HistoryAsync(item.Id, null, 1)).Items.Single().Id;
		Guid root = Guid.NewGuid(), child = Guid.NewGuid();
		TaxonomyRevision head = (await Tree().ReadAsync(null))!;
		TaxonomyRevision revision = await Tree().ChangeAsync(Actor, new(head.Id, Guid.NewGuid(), "Classification fixture",
			[new TaxonomyEdit("create", root, Name: Guid.NewGuid().ToString()), new TaxonomyEdit("create", child, root, "Child")]));
		return (item.Id, revision.Id, root, child, assignment);
	}
	[Fact]
	public async Task BackdatedPurchaseRetainsOriginalPathAndFinancialEvidenceAfterMoveAndReclassification()
	{
		var seed = await SeedAsync();
		await using ApplicationDbContext db = fixture.CreateDbContext();
		ReceiptItemEntity before = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
		DateTimeOffset started = DateTimeOffset.UtcNow;
		PurchaseClassification first = await Service().AssignAsync(Actor, seed.Item, new(seed.Child, seed.Revision, seed.Assignment, Guid.NewGuid(), "Initial choice"));
		first.RecordedAt.Should().BeOnOrAfter(started.AddTicks(-9)); first.Sequence.Should().Be(2);
		TaxonomyRevision moved = await Tree().ChangeAsync(Actor, new(seed.Revision, Guid.NewGuid(), "Move child", [new TaxonomyEdit("move", seed.Child)]));
		PurchaseClassification second = await Service().AssignAsync(Actor, seed.Item, new(seed.Root, moved.Id, first.Id, Guid.NewGuid(), "Broad choice"));
		PurchaseClassification third = await Service().AssignAsync(Actor, seed.Item, new(null, moved.Id, second.Id, Guid.NewGuid(), "Unclassified"));
		third.PreviousAssignmentId.Should().Be(second.Id); third.CategoryId.Should().BeNull(); third.Path.Should().BeEmpty();
		ClassificationHistory history = await Service().HistoryAsync(seed.Item, null, 2);
		history.Items.Select(x => x.Id).Should().Equal(third.Id, second.Id); history.NextBeforeSequence.Should().Be(3);
		ClassificationHistory older = await Service().HistoryAsync(seed.Item, history.NextBeforeSequence, 2);
		older.Items.Select(x => x.Id).Should().Equal(first.Id, seed.Assignment);
		older.Items.First().Path.Select(x => x.Id).Should().Equal(seed.Root, seed.Child);
		ReceiptItemEntity after = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
		after.Should().BeEquivalentTo(before);
		(await db.CurrentClassifications.SingleAsync(x => x.ReceiptItemId == seed.Item)).AssignmentId.Should().Be(third.Id);
		db.Database.HasPendingModelChanges().Should().BeFalse();
		Func<Task> rewrite = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE taxonomy.\"Assignments\" SET \"Reason\" = 'rewrite' WHERE \"Id\" = {first.Id}");
		await rewrite.Should().ThrowAsync<Npgsql.PostgresException>();
	}
	[Fact]
	public async Task ReplayChangedBodyStaleTaxonomyAndCompetingEditsHaveNoDuplicateHistory()
	{
		var seed = await SeedAsync();
		ClassificationCommand command = new(seed.Root, seed.Revision, seed.Assignment, Guid.NewGuid(), "Manual");
		PurchaseClassification first = await Service().AssignAsync(Actor, seed.Item, command);
		(await Service().AssignAsync(Actor, seed.Item, command)).Should().BeEquivalentTo(first);
		Func<Task> changed = () => Service().AssignAsync(Actor, seed.Item, command with { CategoryId = seed.Child });
		(await changed.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		async Task<int> Compete(Guid? target)
		{ try { await Service().AssignAsync(Actor, seed.Item, new(target, seed.Revision, first.Id, Guid.NewGuid(), "Competing edit")); return 200; } catch (TaxonomyException e) { return e.Status; } }
		(await Task.WhenAll(Compete(seed.Child), Compete(null))).Should().BeEquivalentTo([200, 409]);
		TaxonomyRevision archived = await Tree().ChangeAsync(Actor, new(seed.Revision, Guid.NewGuid(), "Archive", [new TaxonomyEdit("archive", seed.Root)]));
		PurchaseClassification current = (await Service().HistoryAsync(seed.Item, null, 1)).Items.Single();
		Func<Task> stale = () => Service().AssignAsync(Actor, seed.Item, new(null, seed.Revision, current.Id, Guid.NewGuid(), "Stale"));
		(await stale.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		Func<Task> unavailable = () => Service().AssignAsync(Actor, seed.Item, new(seed.Root, archived.Id, current.Id, Guid.NewGuid(), "Archived"));
		(await unavailable.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(400);
		(await Service().HistoryAsync(seed.Item, null, 100)).Items.Should().HaveCount(3);
		Func<Task> disabled = () => Service(false).AssignAsync(Actor, seed.Item, new(null, archived.Id, current.Id, Guid.NewGuid(), "Disabled"));
		(await disabled.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
	}
	private sealed class ActorAccessor : Application.Interfaces.Services.ICurrentUserAccessor
	{
		public string? UserId => Actor;
		public Guid? ApiKeyId => null;
		public string? IpAddress => null;
		public string? UserAgent => null;
	}
	private sealed class ActorFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(fixture.CreateOptions(), new ActorAccessor());
	}

	[Fact]
	public async Task AggregateCreateEditAndFailedChoiceKeepFactsAndHistoryAtomic()
	{
		var seed = await SeedAsync();
		var factory = new ActorFactory(fixture);
		var enabled = Options.Create(new ClassificationOptions { WritesEnabled = true });
		var repository = new Infrastructure.Repositories.ReceiptItemRepository(factory, enabled);
		await using var db = fixture.CreateDbContext();
		var receiptId = await db.ReceiptItems.Where(x => x.Id == seed.Item).Select(x => x.ReceiptId).SingleAsync();
		string rootName = await db.TaxonomyNodes.Where(x => x.RevisionId == seed.Revision && x.CategoryId == seed.Root).Select(x => x.Name).SingleAsync();
		var item = ReceiptItemEntityGenerator.Generate(receiptId);
		item.Category = rootName; item.Subcategory = null;
		item.Classification = new(seed.Root, seed.Revision, null, Guid.NewGuid(), "Create with receipt facts");
		Guid createRequest = Guid.NewGuid();
		await repository.CreateAsync([item], [], createRequest, default);
		var first = (await Service().HistoryAsync(item.Id, null, 10)).Items.Single();
		first.Source.Should().Be("manual");
		(await repository.CreateAsync([item], [], createRequest, default)).Single().Id.Should().Be(item.Id);
		(await Service().HistoryAsync(item.Id, null, 10)).Items.Should().ContainSingle();

		string originalDescription = item.Description;
		item.Description = "Edited facts";
		item.Classification = new(null, seed.Revision, first.Id, Guid.NewGuid(), "Clear classification");
		Guid editRequest = Guid.NewGuid();
		await repository.UpdateAsync([item], editRequest, default);
		await repository.UpdateAsync([item], editRequest, default);
		var second = (await Service().HistoryAsync(item.Id, null, 10)).Items.First();
		second.CategoryId.Should().BeNull(); second.Sequence.Should().Be(2);

		item.Description = "Must roll back";
		item.Classification = new(seed.Root, seed.Revision, first.Id, Guid.NewGuid(), "Stale choice");
		Func<Task> rejected = () => repository.UpdateAsync([item], Guid.NewGuid(), default);
		(await rejected.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		(await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == item.Id)).Description.Should().Be("Edited facts");
		(await Service().HistoryAsync(item.Id, null, 10)).Items.Should().HaveCount(2);

		item.Description = originalDescription; item.Classification = null;
		await new Infrastructure.Repositories.ReceiptItemRepository(factory).UpdateAsync([item], default);
		(await Service().HistoryAsync(item.Id, null, 10)).Items.Should().HaveCount(2);
		// A relabel without a choice neither conflicts nor appends, and the stored labels stand.
		item.Category = "Ambiguous legacy edit"; item.Subcategory = "Other";
		await repository.UpdateAsync([item], default);
		(await Service().HistoryAsync(item.Id, null, 10)).Items.Should().HaveCount(2);
		var relabelled = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == item.Id);
		relabelled.Category.Should().Be(rootName); relabelled.Subcategory.Should().BeNull();

		var bad = ReceiptItemEntityGenerator.Generate(receiptId);
		bad.Classification = new(Guid.NewGuid(), seed.Revision, null, Guid.NewGuid(), "Invalid target");
		Func<Task> invalidCreate = async () => { await repository.CreateAsync([bad], [], Guid.NewGuid(), default); };
		(await invalidCreate.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(400);
		(await db.ReceiptItems.AnyAsync(x => x.Id == bad.Id)).Should().BeFalse();
		(await db.Classifications.AnyAsync(x => x.ReceiptItemId == bad.Id)).Should().BeFalse();
	}

	[Fact]
	public async Task CompleteReceiptClassificationFailureRollsBackTheWholeNewReceipt()
	{
		var seed = await SeedAsync();
		await using var db = fixture.CreateDbContext();
		var receiptEntity = ReceiptEntityGenerator.Generate();
		var itemEntity = ReceiptItemEntityGenerator.Generate(receiptEntity.Id);
		var receiptMapper = new Infrastructure.Mapping.ReceiptMapper();
		var itemMapper = new Infrastructure.Mapping.ReceiptItemMapper();
		var item = itemMapper.ToDomain(itemEntity);
		item.Classification = new(Guid.NewGuid(), seed.Revision, null, Guid.NewGuid(), "Invalid category rolls back all facts");
		var writer = new CompleteReceiptWriter(new ActorFactory(fixture), receiptMapper,
			new Infrastructure.Mapping.TransactionMapper(), itemMapper, new Infrastructure.Mapping.AdjustmentMapper(),
			Options.Create(new ClassificationOptions { WritesEnabled = true }));
		int receiptsBefore = await db.Receipts.CountAsync();
		int itemsBefore = await db.ReceiptItems.CountAsync();
		Func<Task> save = async () => { await writer.CreateAsync(receiptMapper.ToDomain(receiptEntity), [], [item], [], Guid.NewGuid(), default); };
		(await save.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(400);
		(await db.Receipts.CountAsync()).Should().Be(receiptsBefore);
		(await db.ReceiptItems.CountAsync()).Should().Be(itemsBefore);
	}

}

