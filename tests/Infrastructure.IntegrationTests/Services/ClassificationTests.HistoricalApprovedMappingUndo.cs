using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task ApprovedUndoRestoresManualNullAndReplayDoesNotReapply()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedUndoReplayCore();
	}

	private async Task ApprovedUndoReplayCore()
	{
		var captured = await CaptureExecutableAsync();
		var executor = ApprovedExecutor();
		var undo = new HistoricalUndoCommand(captured.Request.TaxonomyRevisionId, "Restore prior choice");
		var missing = await Assert.ThrowsAsync<TaxonomyException>(() => executor.UndoChunkAsync(CurrentRestoreGeneration(), "other-actor", captured.Request.OperationId, undo, 1));
		missing.Status.Should().Be(404);
		var manualRequest = await HistoricalRequest(captured.Item, captured.Request.TaxonomyRevisionId, captured.Category);
		await Historical().SavePreviewAsync(manualRequest);
		var wrongKind = await Assert.ThrowsAsync<TaxonomyException>(() => executor.UndoChunkAsync(CurrentRestoreGeneration(), Actor, manualRequest.OperationId, undo, 1));
		wrongKind.Status.Should().Be(409);
		await Assert.ThrowsAsync<ArgumentNullException>(() => executor.UndoChunkAsync(CurrentRestoreGeneration(), Actor, captured.Request.OperationId, null!, 1));
		await using (var before = fixture.CreateDbContext())
		{
			var pending = await before.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == captured.Request.OperationId);
			pending.State.Should().Be(HistoricalItemState.Pending);
			pending.AppliedAssignmentId.Should().BeNull();
		}
		await executor.ApplyChunkAsync(Actor, captured.Request.OperationId, 1);
		var result = await executor.UndoChunkAsync(CurrentRestoreGeneration(), Actor, captured.Request.OperationId, undo, 1);
		result.Undone.Should().Be(1);
		(await executor.UndoChunkAsync(CurrentRestoreGeneration(), Actor, captured.Request.OperationId, undo, 1)).Should().Be(result);
		(await executor.ApplyChunkAsync(Actor, captured.Request.OperationId, 1)).Should().Be(result);
		var history = await Service().HistoryAsync(captured.Item, null, 10);
		history.Items.Should().HaveCount(3);
		history.Items.First().CategoryId.Should().BeNull();
		history.Items.First().Source.Should().Be("manual");
		await using var db = fixture.CreateDbContext();
		var row = await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == captured.Request.OperationId);
		row.UndoAssignmentId.Should().Be(history.Items.First().Id);
		history.Items.First().OperationId.Should().Be(row.UndoOperationId);
		var fresh = captured.Request with { OperationId = Guid.NewGuid() };
		(await new HistoricalApprovedMappingCapture(new Factory(fixture), TimeProvider.System).CaptureAsync(fresh)).Should().Be(0);
		await Assert.ThrowsAsync<TaxonomyException>(() => executor.UndoChunkAsync(CurrentRestoreGeneration(), Actor, captured.Request.OperationId,
				undo with { Reason = "Different intent" }, 1));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ApprovedUndoPreservesLaterManualAssignment(bool sameCategory)
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedUndoLaterCore(sameCategory);
	}

	private async Task ApprovedUndoLaterCore(bool sameCategory)
	{
		var captured = await CaptureExecutableAsync();
		var executor = ApprovedExecutor();
		await executor.ApplyChunkAsync(Actor, captured.Request.OperationId, 1);
		var applied = (await Service().HistoryAsync(captured.Item, null, 1)).Items.Single();
		var later = await Service().AssignAsync(Actor, captured.Item,
				new(sameCategory ? captured.Category : null, captured.Request.TaxonomyRevisionId, applied.Id, Guid.NewGuid(), "Later manual choice"));
		var result = await executor.UndoChunkAsync(CurrentRestoreGeneration(), Actor, captured.Request.OperationId,
				new(captured.Request.TaxonomyRevisionId, "Undo mapping"), 1);
		result.UndoConflicted.Should().Be(1);
		(await Service().HistoryAsync(captured.Item, null, 10)).Items.Should().HaveCount(3).And.Subject.First().Id.Should().Be(later.Id);
	}

	[Fact]
	public async Task ApprovedApplyRetryDuringUndoReportsStoredProgress()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedApplyRetryDuringUndoCore();
	}

	private async Task ApprovedApplyRetryDuringUndoCore()
	{
		var captured = await CaptureExecutableAsync();
		await using var db = fixture.CreateDbContext();
		var original = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == captured.Item);
		var sibling = ReceiptItemEntityGenerator.Generate(original.ReceiptId);
		sibling.Description = original.Description; sibling.ReceiptItemCode = original.ReceiptItemCode;
		sibling.Category = ""; sibling.Subcategory = null;
		await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync([sibling], default);
		var request = captured.Request with { OperationId = Guid.NewGuid() };
		(await new HistoricalApprovedMappingCapture(new Factory(fixture), TimeProvider.System).CaptureAsync(request)).Should().Be(2);
		var executor = ApprovedExecutor();
		(await executor.ApplyChunkAsync(Actor, request.OperationId, 2)).Applied.Should().Be(2);
		var partial = await executor.UndoChunkAsync(CurrentRestoreGeneration(), Actor, request.OperationId, new(request.TaxonomyRevisionId, "Restore"), 1);
		partial.State.Should().Be(HistoricalOperationState.Undoing);
		partial.Undone.Should().Be(1);
		(await executor.ApplyChunkAsync(Actor, request.OperationId, 2)).Should().Be(partial);
		var results = await Task.WhenAll(executor.UndoChunkAsync(CurrentRestoreGeneration(), Actor, request.OperationId, new(request.TaxonomyRevisionId, "Restore"), 1),
				executor.UndoChunkAsync(CurrentRestoreGeneration(), Actor, request.OperationId, new(request.TaxonomyRevisionId, "Restore"), 1));
		results.Should().OnlyContain(x => x.State == HistoricalOperationState.Undone && x.Undone == 2);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ApprovedUndoSerializesWithManualCorrection(bool manualFirst)
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedUndoRaceCore(manualFirst);
	}

	private async Task ApprovedUndoRaceCore(bool manualFirst)
	{
		var captured = await CaptureExecutableAsync();
		await ApprovedExecutor().ApplyChunkAsync(Actor, captured.Request.OperationId, 1);
		var applied = (await Service().HistoryAsync(captured.Item, null, 1)).Items.Single();
		var winner = new HistoricalRaceGate(true);
		var loser = new HistoricalRaceGate(false);
		var executor = new HistoricalApprovedMappingExecutor(new HistoricalRaceFactory(fixture, manualFirst ? loser : winner), TimeProvider.System,
				Options.Create(new ClassificationOptions { WritesEnabled = true }));
		var manual = new ClassificationService(new HistoricalRaceFactory(fixture, manualFirst ? winner : loser), TimeProvider.System,
				Options.Create(new ClassificationOptions { WritesEnabled = true }));
		var correction = new ClassificationCommand(captured.Category, captured.Request.TaxonomyRevisionId, applied.Id, Guid.NewGuid(), "Manual same category");
		var undo = new HistoricalUndoCommand(captured.Request.TaxonomyRevisionId, "Undo mapping");
		Task<PurchaseClassification>? edit = null;
		Task<HistoricalProgress>? reverting = null;
		if (manualFirst) { edit = manual.AssignAsync(Actor, captured.Item, correction); }
		else { reverting = executor.UndoChunkAsync(CurrentRestoreGeneration(), Actor, captured.Request.OperationId, undo, 1); }
		try
		{
			await winner.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
			if (manualFirst) { reverting = executor.UndoChunkAsync(CurrentRestoreGeneration(), Actor, captured.Request.OperationId, undo, 1); }
			else { edit = manual.AssignAsync(Actor, captured.Item, correction); }
			await AssertHistoricalDatabaseWait(winner, loser);
		}
		finally { winner.Release.TrySetResult(); }
		if (manualFirst)
		{
			var later = await edit!;
			(await reverting!).UndoConflicted.Should().Be(1);
			(await Service().HistoryAsync(captured.Item, null, 1)).Items.Single().Id.Should().Be(later.Id);
		}
		else
		{
			(await reverting!).Undone.Should().Be(1);
			var error = await Assert.ThrowsAsync<TaxonomyException>(async () => await edit!);
			error.Status.Should().Be(409);
			(await Service().HistoryAsync(captured.Item, null, 1)).Items.Single().CategoryId.Should().BeNull();
		}
	}

	private sealed class FailApprovedUndoDb(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
	{
		public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
		{
			if (ChangeTracker.Entries<HistoricalOperationItemEntity>().Any(x => x.Entity.UndoState == HistoricalItemState.Applied))
			{ throw new InvalidOperationException("Injected undo outcome failure"); }
			return base.SaveChangesAsync(cancellationToken);
		}
	}
	private sealed class FailApprovedUndoFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
	{ public ApplicationDbContext CreateDbContext() => new FailApprovedUndoDb(fixture.CreateOptions()); }

	[Fact]
	public async Task ApprovedUndoRollsBackAssignmentAndOutcomeTogether()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedUndoRollbackCore();
	}

	private async Task ApprovedUndoRollbackCore()
	{
		var captured = await CaptureExecutableAsync();
		await ApprovedExecutor().ApplyChunkAsync(Actor, captured.Request.OperationId, 1);
		var undo = new HistoricalUndoCommand(captured.Request.TaxonomyRevisionId, "Restore prior choice");
		var failing = new HistoricalApprovedMappingExecutor(new FailApprovedUndoFactory(fixture), TimeProvider.System,
				Options.Create(new ClassificationOptions { WritesEnabled = true }));
		await Assert.ThrowsAsync<InvalidOperationException>(() => failing.UndoChunkAsync(CurrentRestoreGeneration(), Actor, captured.Request.OperationId, undo, 1));
		(await Service().HistoryAsync(captured.Item, null, 10)).Items.Should().HaveCount(2);
		await using var db = fixture.CreateDbContext();
		var row = await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == captured.Request.OperationId);
		row.UndoState.Should().Be(HistoricalItemState.Pending);
		row.UndoAssignmentId.Should().BeNull();
		(await ApprovedExecutor().UndoChunkAsync(CurrentRestoreGeneration(), Actor, captured.Request.OperationId, undo, 1)).Undone.Should().Be(1);
	}
}
