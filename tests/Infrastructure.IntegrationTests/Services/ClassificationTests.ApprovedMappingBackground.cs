using System.Data.Common;
using Application.Normalization;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	private ApprovedMappingBackgroundWorker MappingWorker(bool enabled = true) => new(new Factory(fixture), TimeProvider.System,
		Options.Create(new ClassificationOptions { WritesEnabled = enabled }), NullLogger<ApprovedMappingBackgroundWorker>.Instance);

	[Theory]
	[InlineData("none")]
	[InlineData("failure")]
	[InlineData("timeout")]
	public async Task BackgroundMappingRotatesUnfinishedSourcesAfterEachBoundedPass(string failure)
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).BackgroundMappingFairnessCore(failure);
	}

	private sealed class MappingPassFailure(string failure) : DbCommandInterceptor
	{
		private int gates;
		public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
			CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("pg_advisory_xact_lock") && command.Parameters.Count == 2 &&
				++gates == 2)
			{
				if (failure == "failure") { throw new InvalidOperationException("Injected apply failure"); }
				if (failure == "timeout") { throw new OperationCanceledException("Injected pass timeout"); }
			}
			return ValueTask.FromResult(result);
		}
	}

	private sealed class MappingFailureFactory(PostgresFixture database, MappingPassFailure failure)
		: IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>(database.CreateOptions())
			.AddInterceptors(failure).Options);
	}

	private async Task BackgroundMappingFairnessCore(string failure)
	{
		var captured = await CaptureExecutableAsync();
		await using var db = fixture.CreateDbContext();
		var original = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == captured.Item);
		string otherDescription = "fairness-" + Guid.NewGuid();
		var items = Enumerable.Range(0, 410).Select(n =>
		{
			var item = ReceiptItemEntityGenerator.Generate(original.ReceiptId);
			item.Description = n < 205 ? original.Description : otherDescription;
			item.ReceiptItemCode = original.ReceiptItemCode;
			item.Category = ""; item.Subcategory = null;
			return item;
		}).ToList();
		await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync(items, default);
		var source = new NormalizationSourceKey(captured.Request.Group.Store, otherDescription, original.ReceiptItemCode);
		var other = await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor,
			new(source, NormalizationCatalogPolicy.Hash(source), null, captured.Request.TaxonomyRevisionId,
				captured.Category, Guid.NewGuid(), "Approve second source"));
		using var worker = new ApprovedMappingBackgroundWorker(new MappingFailureFactory(fixture, new(failure)), TimeProvider.System,
			Options.Create(new ClassificationOptions { WritesEnabled = true }), NullLogger<ApprovedMappingBackgroundWorker>.Instance);
		if (failure == "none") { await worker.RunOnceAsync(); }
		else if (failure == "failure") { await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunOnceAsync()); }
		else { await Assert.ThrowsAsync<OperationCanceledException>(() => worker.RunOnceAsync()); }
		Guid first = await db.Set<HistoricalMappingSweepEntity>().Join(db.HistoricalOperations,
			x => x.PendingOperationId, x => x.Id, (sweep, operation) => operation.ApprovedMappingRevisionId!.Value).SingleAsync();
		Guid second = first == other.Id ? captured.Request.MappingRevisionId : other.Id;
		await worker.RunOnceAsync();
		(await db.HistoricalOperationItems.CountAsync(x => x.ApprovedMappingRevisionId == second && x.AppliedAssignmentId != null))
			.Should().Be(100, "the other source gets a turn while the first has unfinished work");
		await worker.RunOnceAsync();
		(await db.HistoricalOperationItems.CountAsync(x => x.ApprovedMappingRevisionId == first && x.AppliedAssignmentId != null))
			.Should().Be(failure == "none" ? 200 : 100, "the first source resumes its frozen page after yielding");
	}

	[Fact]
	public async Task BackgroundMappingRepeatedIdleScansDoNotGrowDurableHistory()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).BackgroundMappingIdleCore();
	}

	private async Task BackgroundMappingIdleCore()
	{
		await CaptureExecutableAsync();
		using var worker = MappingWorker();
		await worker.RunOnceAsync();
		await worker.RunOnceAsync();
		await using var db = fixture.CreateDbContext();
		int sweeps = await db.Set<HistoricalMappingSweepEntity>().CountAsync();
		int operations = await db.HistoricalOperations.CountAsync();
		int items = await db.HistoricalOperationItems.CountAsync();
		for (int n = 0; n < 20; n++)
		{
			await worker.RunOnceAsync();
			using var restarted = MappingWorker();
			await restarted.RunOnceAsync();
		}
		(await db.Set<HistoricalMappingSweepEntity>().CountAsync()).Should().Be(sweeps);
		(await db.HistoricalOperations.CountAsync()).Should().Be(operations);
		(await db.HistoricalOperationItems.CountAsync()).Should().Be(items);
	}

	[Fact]
	public async Task BackgroundMappingResumesPagesWithoutProviderAndPublicUndoPreservesManualClear()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).BackgroundMappingCore();
	}

	private async Task BackgroundMappingCore()
	{
		var captured = await CaptureExecutableAsync();
		await using var db = fixture.CreateDbContext();
		var original = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == captured.Item);
		var items = Enumerable.Range(0, 205).Select(_ =>
		{
			var item = ReceiptItemEntityGenerator.Generate(original.ReceiptId);
			item.Description = original.Description; item.ReceiptItemCode = original.ReceiptItemCode;
			item.Category = ""; item.Subcategory = null;
			return item;
		}).ToArray();
		await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync(items.ToList(), default);
		await db.Database.ExecuteSqlRawAsync("UPDATE category_assistance.\"Settings\" SET \"Enabled\" = false");
		using (var disabled = MappingWorker(false)) { await disabled.RunOnceAsync(); }
		(await db.Classifications.CountAsync(x => x.Source == "approved_mapping")).Should().Be(0);
		using (var worker = MappingWorker()) { await worker.RunOnceAsync(); }
		(await db.Classifications.CountAsync(x => x.Source == "approved_mapping")).Should().Be(100);
		// Recreate the worker after every page: cursor and frozen pending page are durable.
		for (int n = 0; n < 4; n++)
		{
			using var restarted = MappingWorker();
			await restarted.RunOnceAsync();
		}
		(await db.Classifications.CountAsync(x => x.Source == "approved_mapping")).Should().Be(206);
		var operation = await db.HistoricalOperations.AsNoTracking()
			.Where(x => x.Kind == Infrastructure.Entities.Taxonomy.HistoricalOperationKind.ApprovedMappingV1 &&
				db.HistoricalOperationItems.Any(i => i.OperationId == x.Id && i.AppliedAssignmentId != null))
			.OrderBy(x => x.CapturedAt).FirstAsync();
		var service = new HistoricalReclassificationService(new Factory(fixture), TimeProvider.System,
			Options.Create(new ClassificationOptions { WritesEnabled = true }));
		var undone = await service.UndoChunkAsync(Actor, operation.Id, new(captured.Request.TaxonomyRevisionId, "Undo automatic mapping"), 100);
		undone.State.Should().Be(HistoricalOperationState.Undone);
		var undoneItems = await db.HistoricalOperationItems.AsNoTracking().Where(x => x.OperationId == operation.Id).Select(x => x.ItemId).ToArrayAsync();
		for (int n = 0; n < 8; n++)
		{
			using var restarted = MappingWorker();
			await restarted.RunOnceAsync();
		}
		foreach (Guid item in undoneItems)
		{
			var current = (await Service().HistoryAsync(item, null, 1)).Items.Single();
			current.CategoryId.Should().BeNull();
			current.Source.Should().Be("manual");
		}
	}

	[Fact]
	public async Task BackgroundMappingResumesFrozenPageAndKeepsLaterManualChoice()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).BackgroundMappingFrozenCore();
	}

	private async Task BackgroundMappingFrozenCore()
	{
		var captured = await CaptureExecutableAsync();
		var sweep = new ApprovedMappingSweepRequest(Guid.NewGuid(), Actor, captured.Request.TaxonomyRevisionId,
			captured.Request.MappingRevisionId, captured.Request.Group, [], captured.Request.ExpectedRestoreGeneration);
		var page = await new HistoricalApprovedMappingSweep(new Factory(fixture), TimeProvider.System).NextAsync(sweep);
		var correction = await Service().AssignAsync(Actor, captured.Item,
			new(null, captured.Request.TaxonomyRevisionId, captured.Assignment, Guid.NewGuid(), "Keep unclassified"));
		using var worker = MappingWorker();
		await worker.RunOnceAsync();
		(await Service().HistoryAsync(captured.Item, null, 1)).Items.Single().Id.Should().Be(correction.Id);
		await using var db = fixture.CreateDbContext();
		var row = await db.HistoricalOperationItems.SingleAsync(x => x.OperationId == page.OperationId);
		row.State.Should().Be(HistoricalItemState.Skipped);
	}
}

