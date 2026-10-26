using System.Data.Common;
using Application.Taxonomy;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{

	[Fact]
	public async Task HistoricalGroupsRankAbsoluteUsdImpactBeforePagingWithoutDroppingOtherCurrencyMembers()
	{
		var seed = await HistoricalSeedAsync();
		string prefix = $"Impact-{Guid.NewGuid():N}-";
		await using ApplicationDbContext db = fixture.CreateDbContext();
		Guid receiptId = (await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item)).ReceiptId;
		ReceiptItemEntity Make(string suffix, decimal amount)
		{
			ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receiptId);
			item.Description = prefix + suffix;
			item.ReceiptItemCode = null;
			item.TotalAmount = amount;
			item.TotalAmountCurrency = Common.Currency.USD;
			return item;
		}
		ReceiptItemEntity[] added = [Make("A", 5), Make("A", -5), Make("B", 20), Make("C", 20), Make("D", 1000)];
		db.ReceiptItems.AddRange(added);
		await db.SaveChangesAsync();
		try
		{
			// Historical imports may contain another stored currency even while USD is the only selectable one.
			await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE receipts.\"ReceiptItems\" SET \"TotalAmountCurrency\" = 'EUR' WHERE \"Id\" = {added[4].Id}");
			HistoricalGroupReader reader = new(new Factory(fixture));
			var frequency = await reader.BrowseAsync(new(Search: prefix, Actor: Actor));
			frequency.Items.Select(x => x.Group.RawDescription).Should().Equal(prefix + "A", prefix + "B", prefix + "C", prefix + "D");
			frequency.Items[0].PurchaseCount.Should().Be(2);
			frequency.Items[0].ImpactAmount.Should().Be(10);
			frequency.Items[3].PurchaseCount.Should().Be(1);
			frequency.Items[3].ImpactAmount.Should().Be(0);
			var impact = await reader.BrowseAsync(new(Search: prefix, Actor: Actor, Sort: "largestAmount"));
			impact.Items.Select(x => x.Group.RawDescription).Should().Equal(prefix + "B", prefix + "C", prefix + "A", prefix + "D");
			impact.Items.Select(x => x.ImpactAmount).Should().Equal(20, 20, 10, 0);
			var next = await reader.BrowseAsync(new(Offset: 1, Limit: 1, Search: prefix, Actor: Actor, Sort: "largestAmount"));
			next.Items.Single().Group.RawDescription.Should().Be(prefix + "C");
			next.TotalGroups.Should().Be(4);
			next.NextOffset.Should().Be(2);
		}
		finally
		{
			await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM receipts.\"ReceiptItems\" WHERE \"Description\" LIKE {prefix + "%"}");
		}
	}
	private HistoricalReclassificationService Historical(IDbContextFactory<ApplicationDbContext>? factory = null) =>
		new(factory ?? new Factory(fixture), TimeProvider.System, Options.Create(new ClassificationOptions { WritesEnabled = true }));

	private async Task<(Guid Item, Guid Revision, Guid Root, Guid Child, Guid Assignment)> HistoricalSeedAsync()
	{
		var seed = await SeedAsync();
		await using ApplicationDbContext db = fixture.CreateDbContext();
		var item = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
		item.Description = $"Historical fixture {Guid.NewGuid():N}";
		await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).UpdateAsync([item], default);
		return seed;
	}

	private async Task<HistoricalPreviewRequest> HistoricalRequest(Guid itemId, Guid revision, Guid? target)
	{
		await using ApplicationDbContext db = fixture.CreateDbContext();
		var source = await (from item in db.ReceiptItems
							join receipt in db.Receipts on item.ReceiptId equals receipt.Id
							where item.Id == itemId
							select new { receipt.Location, item.Description, item.ReceiptItemCode }).SingleAsync();
		return new(Guid.NewGuid(), Actor, revision, "Review exact historical group",
			[new(new(source.Location, source.Description, source.ReceiptItemCode), target)]);
	}

	[Fact]
	public async Task HistoricalPreviewFreezesServerEvidenceAndReplayCannotChangeIntent()
	{
		var seed = await HistoricalSeedAsync();
		HistoricalPreviewRequest request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		HistoricalProgress preview = await Historical().SavePreviewAsync(request);
		preview.Total.Should().Be(1);
		var frozen = (await Historical().ReadItemsAsync(Actor, request.OperationId, 0, 100)).Items.Single();
		Guid? previousCategory = (await Service().HistoryAsync(seed.Item, null, 1)).Items.Single().CategoryId;
		frozen.Item.PreviousCategoryId.Should().Be(previousCategory);
		frozen.Item.ExpectedAssignmentId.Should().Be(seed.Assignment);
		(await Historical().SavePreviewAsync(request)).Should().Be(preview);
		Func<Task> changed = () => Historical().SavePreviewAsync(request with { Reason = "Changed" });
		(await changed.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
		Func<Task> stranger = () => Historical().ReadAsync("other actor", request.OperationId);
		(await stranger.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(404);
		HistoricalProgress completed = await Historical().ApplyChunkAsync(Actor, request.OperationId, 100);
		completed.Applied.Should().Be(1);
		completed.State.Should().Be(HistoricalOperationState.Completed);
		(await Historical().ApplyChunkAsync(Actor, request.OperationId, 100)).Should().Be(completed);
		(await Service().HistoryAsync(seed.Item, null, 100)).Items.Should().HaveCount(2);
		HistoricalProgress undone = await Historical().UndoChunkAsync(Actor, request.OperationId, new(seed.Revision, "Restore previous choice"), 100);
		undone.Undone.Should().Be(1);
		(await Service().HistoryAsync(seed.Item, null, 1)).Items.Single().CategoryId.Should().Be(previousCategory);
		(await Historical().UndoChunkAsync(Actor, request.OperationId, new(seed.Revision, "Restore previous choice"), 100)).Should().Be(undone);
		Func<Task> changedUndo = () => Historical().UndoChunkAsync(Actor, request.OperationId, new(seed.Revision, "Different reason"), 100);
		(await changedUndo.Should().ThrowAsync<TaxonomyException>()).Which.Status.Should().Be(409);
	}

	[Fact]
	public async Task HistoricalSourceAndAssignmentFencesAreIndependentAndUndoPreservesLaterEdit()
	{
		var seed = await HistoricalSeedAsync();
		HistoricalPreviewRequest source = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(source);
		await using (ApplicationDbContext db = fixture.CreateDbContext())
		{
			await db.ReceiptItems.Where(x => x.Id == seed.Item).ExecuteUpdateAsync(s => s.SetProperty(x => x.Description, "Source changed after preview"));
		}
		(await Historical().ApplyChunkAsync(Actor, source.OperationId, 1)).Conflicted.Should().Be(1);
		(await Historical().ReadItemsAsync(Actor, source.OperationId, 0, 1)).Items.Single().Reason.Should().Be("source-changed");
		HistoricalPreviewRequest assignment = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(assignment);
		await Service().AssignAsync(Actor, seed.Item, new(seed.Child, seed.Revision, seed.Assignment, Guid.NewGuid(), "Later manual refinement"));
		(await Historical().ApplyChunkAsync(Actor, assignment.OperationId, 1)).Conflicted.Should().Be(1);
		(await Historical().ReadItemsAsync(Actor, assignment.OperationId, 0, 1)).Items.Single().Reason.Should().Be("assignment-changed");
		HistoricalPreviewRequest owned = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(owned);
		await Historical().ApplyChunkAsync(Actor, owned.OperationId, 1);
		var applied = (await Service().HistoryAsync(seed.Item, null, 1)).Items.Single();
		var later = await Service().AssignAsync(Actor, seed.Item, new(seed.Child, seed.Revision, applied.Id, Guid.NewGuid(), "Keep later choice"));
		var back = await Service().AssignAsync(Actor, seed.Item, new(seed.Root, seed.Revision, later.Id, Guid.NewGuid(), "Later edit returns to applied category"));
		(await Historical().UndoChunkAsync(Actor, owned.OperationId, new(seed.Revision, "Undo only owned choice"), 1)).UndoConflicted.Should().Be(1);
		(await Service().HistoryAsync(seed.Item, null, 1)).Items.Single().Id.Should().Be(back.Id);
	}

	private sealed class FailHistoricalProgressDb(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
	{
		public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
		{
			if (ChangeTracker.Entries<HistoricalOperationItemEntity>().Any(x => x.Entity.State == HistoricalItemState.Applied))
			{ throw new InvalidOperationException("Injected failure after assignment save and before progress commit"); }
			return base.SaveChangesAsync(cancellationToken);
		}
	}
	private sealed class FailHistoricalFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => new FailHistoricalProgressDb(fixture.CreateOptions()); }

	[Fact]
	public async Task HistoricalCrashRollsBackAssignmentAndProgressThenFreshRunnerResumesAndCancelStopsWork()
	{
		var seed = await HistoricalSeedAsync();
		HistoricalPreviewRequest request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		Func<Task> crash = () => Historical(new FailHistoricalFactory(fixture)).ApplyChunkAsync(Actor, request.OperationId, 1);
		await crash.Should().ThrowAsync<InvalidOperationException>();
		(await Historical().ReadAsync(Actor, request.OperationId)).Pending.Should().Be(1);
		(await Service().HistoryAsync(seed.Item, null, 100)).Items.Should().ContainSingle();
		(await Historical().ApplyChunkAsync(Actor, request.OperationId, 1)).Applied.Should().Be(1);
		(await Service().HistoryAsync(seed.Item, null, 100)).Items.Should().HaveCount(2);
		HistoricalPreviewRequest cancelled = await HistoricalRequest(seed.Item, seed.Revision, seed.Child);
		await Historical().SavePreviewAsync(cancelled);
		await Historical().CancelAsync(Actor, cancelled.OperationId);
		HistoricalProgress stopped = await Historical().ApplyChunkAsync(Actor, cancelled.OperationId, 100);
		stopped.State.Should().Be(HistoricalOperationState.Cancelled);
		stopped.Pending.Should().Be(1);
		stopped.Applied.Should().Be(0);
	}

	private sealed class HistoricalQueryCapture : DbCommandInterceptor
	{
		public List<(string Sql, int[] IntegerParameters)> PendingQueries { get; } = [];
		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
			CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("HistoricalOperationItems") && command.CommandText.Contains("ORDER BY"))
			{
				PendingQueries.Add((command.CommandText,
					command.Parameters.Cast<DbParameter>().Where(x => x.Value is int).Select(x => (int)x.Value!).ToArray()));
			}
			return ValueTask.FromResult(result);
		}
	}

	private sealed class HistoricalQueryFactory(PostgresFixture fixture, HistoricalQueryCapture capture) : IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
			.AddInterceptors(capture).Options);
	}

	[Fact]
	public async Task HistoricalChunksSelectAtMost100AndConcurrentRetriesNeverDuplicateAssignments()
	{
		var seed = await HistoricalSeedAsync();
		await using ApplicationDbContext db = fixture.CreateDbContext();
		var source = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
		var items = Enumerable.Range(0, 100).Select(_ =>
		{
			var item = SampleData.Entities.ReceiptItemEntityGenerator.Generate(source.ReceiptId);
			item.Description = source.Description;
			item.ReceiptItemCode = source.ReceiptItemCode;
			return item;
		}).ToList();
		await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync(items, default);
		HistoricalPreviewRequest request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		(await Historical().SavePreviewAsync(request)).Total.Should().Be(101);
		var capture = new HistoricalQueryCapture();
		HistoricalProgress first = await Historical(new HistoricalQueryFactory(fixture, capture)).ApplyChunkAsync(Actor, request.OperationId, 100);
		var selection = capture.PendingQueries.Should().ContainSingle().Subject;
		selection.Sql.Should().Contain("LIMIT");
		selection.IntegerParameters.Should().Contain(100);
		first.Applied.Should().Be(100);
		first.Pending.Should().Be(1);
		first.State.Should().Be(HistoricalOperationState.Applying);
		await Task.WhenAll(Historical().ApplyChunkAsync(Actor, request.OperationId, 100), Historical().ApplyChunkAsync(Actor, request.OperationId, 100));
		HistoricalProgress completed = await Historical().ReadAsync(Actor, request.OperationId);
		completed.Applied.Should().Be(101);
		completed.Pending.Should().Be(0);
		completed.State.Should().Be(HistoricalOperationState.Completed);
		(await db.HistoricalOperationItems.Where(x => x.OperationId == request.OperationId).Select(x => x.AppliedAssignmentId).Distinct().CountAsync()).Should().Be(101);
		(await (from current in db.CurrentClassifications
				join row in db.HistoricalOperationItems on current.ReceiptItemId equals row.ItemId
				join assignment in db.Classifications on current.AssignmentId equals assignment.Id
				where row.OperationId == request.OperationId && assignment.CategoryId == seed.Root
					&& current.AssignmentId == row.AppliedAssignmentId
				select current.ReceiptItemId).CountAsync()).Should().Be(101);
		(await (from assignment in db.Classifications
				join row in db.HistoricalOperationItems on assignment.ReceiptItemId equals row.ItemId
				where row.OperationId == request.OperationId
				select assignment.Id).CountAsync()).Should().Be(202);
		var page = await Historical().ReadItemsAsync(Actor, request.OperationId, 0, 100);
		page.Items.Should().HaveCount(100);
		page.NextOffset.Should().Be(100);
		(await Historical().ReadItemsAsync(Actor, request.OperationId, page.NextOffset!.Value, 100)).Items.Should().ContainSingle();
		var undo = new HistoricalUndoCommand(seed.Revision, "Restore entire exact group");
		(await Historical().UndoChunkAsync(Actor, request.OperationId, undo, 100)).Undone.Should().Be(100);
		await Task.WhenAll(Historical().UndoChunkAsync(Actor, request.OperationId, undo, 100),
			Historical().UndoChunkAsync(Actor, request.OperationId, undo, 100));
		var undone = await Historical().ReadAsync(Actor, request.OperationId);
		undone.Undone.Should().Be(101);
		undone.State.Should().Be(HistoricalOperationState.Undone);
		(await (from current in db.CurrentClassifications
				join row in db.HistoricalOperationItems on current.ReceiptItemId equals row.ItemId
				join assignment in db.Classifications on current.AssignmentId equals assignment.Id
				where row.OperationId == request.OperationId && current.AssignmentId == row.UndoAssignmentId
					&& assignment.CategoryId == row.PreviousCategoryId
				select current.ReceiptItemId).CountAsync()).Should().Be(101);
		(await (from assignment in db.Classifications
				join row in db.HistoricalOperationItems on assignment.ReceiptItemId equals row.ItemId
				where row.OperationId == request.OperationId
				select assignment.Id).CountAsync()).Should().Be(303);
	}
	[Fact]
	public async Task HistoricalUndoRestoresExplicitNullCategory()
	{
		var seed = await HistoricalSeedAsync();
		var unclassified = await Service().AssignAsync(Actor, seed.Item,
			new(null, seed.Revision, seed.Assignment, Guid.NewGuid(), "Explicitly unclassify before capture"));
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		var frozen = (await Historical().ReadItemsAsync(Actor, request.OperationId, 0, 1)).Items.Single();
		frozen.Item.ExpectedAssignmentId.Should().Be(unclassified.Id);
		frozen.Item.PreviousCategoryId.Should().BeNull();
		(await Historical().ApplyChunkAsync(Actor, request.OperationId, 1)).Applied.Should().Be(1);
		(await Historical().UndoChunkAsync(Actor, request.OperationId, new(seed.Revision, "Restore null"), 1)).Undone.Should().Be(1);
		var restored = (await Service().HistoryAsync(seed.Item, null, 1)).Items.Single();
		restored.CategoryId.Should().BeNull();
		restored.Sequence.Should().Be(4);
	}

	[Fact]
	public async Task HistoricalTaxonomyFenceDoesNotRequireSourceOrAssignmentChanges()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		await Tree().ChangeAsync(Actor, new(seed.Revision, Guid.NewGuid(), "Independent taxonomy revision",
			[new TaxonomyEdit("create", Guid.NewGuid(), Name: Guid.NewGuid().ToString())]));
		(await Historical().ApplyChunkAsync(Actor, request.OperationId, 1)).Conflicted.Should().Be(1);
		(await Historical().ReadItemsAsync(Actor, request.OperationId, 0, 1)).Items.Single().Reason.Should().Be("taxonomy-changed");
		(await Service().HistoryAsync(seed.Item, null, 100)).Items.Should().ContainSingle().Which.Id.Should().Be(seed.Assignment);
	}

	[Fact]
	public async Task HistoricalSourceEditAwayAndBackStillConflictsOnRevision()
	{
		var seed = await HistoricalSeedAsync();
		var request = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(request);
		await using ApplicationDbContext db = fixture.CreateDbContext();
		var item = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
		string original = item.Description;
		var repository = new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture));
		item.Description = "Temporary source edit";
		await repository.UpdateAsync([item], default);
		item.Description = original;
		await repository.UpdateAsync([item], default);
		(await Historical().ApplyChunkAsync(Actor, request.OperationId, 1)).Conflicted.Should().Be(1);
		(await Historical().ReadItemsAsync(Actor, request.OperationId, 0, 1)).Items.Single().Reason.Should().Be("source-changed");
		(await Service().HistoryAsync(seed.Item, null, 100)).Items.Should().ContainSingle();
	}

}

