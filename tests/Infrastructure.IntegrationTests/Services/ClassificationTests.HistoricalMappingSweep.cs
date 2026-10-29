using Application.Normalization;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task ApprovedMappingSweepAdvancesPastSkippedPagesAndReplaysPendingWork()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedMappingSweepCore();
	}

	private async Task ApprovedMappingSweepCore()
	{
		var seed = await HistoricalSeedAsync();
		var group = (await HistoricalRequest(seed.Item, seed.Revision, seed.Root)).Groups.Single().Group;
		await using var db = fixture.CreateDbContext();
		var original = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
		var items = Enumerable.Range(0, 205).Select(_ =>
		{
			var item = ReceiptItemEntityGenerator.Generate(original.ReceiptId);
			item.Description = original.Description; item.ReceiptItemCode = original.ReceiptItemCode;
			item.Category = ""; item.Subcategory = null; return item;
		}).ToList();
		await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync(items, default);
		var source = new NormalizationSourceKey(group.Store, group.RawDescription, group.ItemCode);
		var mapping = await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor,
			new(source, NormalizationCatalogPolicy.Hash(source), null, seed.Revision, seed.Root, Guid.NewGuid(), "Approve"));
		Guid generation = CurrentRestoreGeneration();
		var request = new ApprovedMappingSweepRequest(Guid.NewGuid(), Actor, seed.Revision, mapping.Id, group, [], generation);
		var sweep = new HistoricalApprovedMappingSweep(new Factory(fixture), TimeProvider.System);
		int beforeEmpty = await db.HistoricalOperations.CountAsync();
		var emptyRequest = request with { SweepId = Guid.NewGuid(), ExcludedItemIds = items.Select(x => x.Id).ToArray() };
		var empty = await sweep.NextAsync(emptyRequest);
		empty.Completed.Should().BeTrue();
		empty.OperationId.Should().BeNull();
		(await sweep.NextAsync(emptyRequest)).Should().Be(empty);
		(await db.HistoricalOperations.CountAsync()).Should().Be(beforeEmpty, "an initially empty sweep must not persist an operation");
		string backup = await AcceptanceExport();
		File.Delete(backup);
		var first = await sweep.NextAsync(request);
		first.Count.Should().Be(100);
		(await sweep.NextAsync(request)).Should().Be(first, "a crash before apply must resume the same operation");
		var firstRows = await db.HistoricalOperationItems.AsNoTracking().Where(x => x.OperationId == first.OperationId).ToListAsync();
		var manualRow = firstRows[0];
		var attempt = new Infrastructure.Entities.CategoryAssistance.CategoryAssistanceAttemptEntity
		{
			Id = Guid.NewGuid(),
			RequestId = Guid.NewGuid(),
			RequestScope = Guid.NewGuid(),
			SourceHash = "synthetic",
			Location = group.Store,
			Description = group.RawDescription,
			ItemCodeKey = group.ItemCode ?? "",
			State = "suggested"
		};
		db.CategoryAssistanceAttempts.Add(attempt);
		db.CategoryAssistanceReviewPreferences.Add(new()
		{
			AttemptId = attempt.Id,
			Location = attempt.Location,
			Description = attempt.Description,
			ItemCodeKey = attempt.ItemCodeKey,
			Actor = Actor,
			State = "active",
			ExcludedItemIdsJson = System.Text.Json.JsonSerializer.Serialize(firstRows.Skip(1).Select(x => x.ItemId).ToArray()),
			Revision = Guid.NewGuid()
		});
		await db.SaveChangesAsync();
		var winner = new HistoricalRaceGate(true); var loser = new HistoricalRaceGate(false);
		var correctionService = new ClassificationService(new HistoricalRaceFactory(fixture, winner), TimeProvider.System,
			Options.Create(new ClassificationOptions { WritesEnabled = true }));
		var executor = new HistoricalApprovedMappingExecutor(new HistoricalRaceFactory(fixture, loser), TimeProvider.System,
			Options.Create(new ClassificationOptions { WritesEnabled = true }));
		var correctionTask = correctionService.AssignAsync(Actor, manualRow.ItemId,
			new(null, seed.Revision, manualRow.ExpectedAssignmentId, Guid.NewGuid(), "Concurrent explicit clear"));
		Task<Application.Taxonomy.HistoricalProgress>? applying = null;
		try
		{
			await winner.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
			applying = executor.ApplyChunkAsync(generation, Actor, first.OperationId!.Value, 100);
			await AssertHistoricalDatabaseWait(winner, loser);
		}
		finally { winner.Release.TrySetResult(); }
		var correction = await correctionTask;
		(await applying!).Skipped.Should().Be(100);
		(await Service().HistoryAsync(manualRow.ItemId, null, 1)).Items.Single().Id.Should().Be(correction.Id);
		(await ApprovedExecutor().ApplyChunkAsync(generation, Actor, first.OperationId.Value, 100)).Skipped.Should().Be(100);
		var second = await sweep.NextAsync(request);
		second.Count.Should().Be(100);
		second.OperationId!.Value.Should().NotBe(first.OperationId!.Value);
		(await sweep.NextAsync(request)).Should().Be(second);
		(await ApprovedExecutor().ApplyChunkAsync(generation, Actor, second.OperationId!.Value, 100)).Applied.Should().Be(100);
		var third = await sweep.NextAsync(request);
		third.Count.Should().Be(5);
		(await ApprovedExecutor().ApplyChunkAsync(generation, Actor, third.OperationId!.Value, 100)).Applied.Should().Be(5);
		int beforeExhaustion = await db.HistoricalOperations.CountAsync();
		(await sweep.NextAsync(request)).Completed.Should().BeTrue();
		(await sweep.NextAsync(request)).Completed.Should().BeTrue();
		(await db.HistoricalOperations.CountAsync()).Should().Be(beforeExhaustion, "exhaustion must not persist an empty operation");
		var rows = await db.HistoricalOperationItems.AsNoTracking().Where(x => x.OperationId == first.OperationId ||
			x.OperationId == second.OperationId || x.OperationId == third.OperationId).ToListAsync();
		rows.Select(x => x.ItemId).Distinct().Should().HaveCount(205);
		(await Assert.ThrowsAsync<Application.Taxonomy.TaxonomyException>(() => sweep.NextAsync(request with { Actor = "another" }))).Status.Should().Be(409);
		// A later sweep revisits the protected items without reapplying the manual clear.
		var laterRequest = request with { SweepId = Guid.NewGuid() };
		var later = await sweep.NextAsync(laterRequest);
		later.Count.Should().Be(99);
		var settings = await db.CategoryAssistanceSettings.SingleAsync();
		settings.RestoreGeneration = Guid.NewGuid();
		await db.SaveChangesAsync();
		(await Assert.ThrowsAsync<Application.Taxonomy.TaxonomyException>(() => sweep.NextAsync(laterRequest))).Status.Should().Be(409);
		(await Assert.ThrowsAsync<Application.Taxonomy.TaxonomyException>(() => sweep.NextAsync(laterRequest with { ExpectedRestoreGeneration = settings.RestoreGeneration }))).Status.Should().Be(409);
		(await Assert.ThrowsAsync<Application.Taxonomy.TaxonomyException>(() => ApprovedExecutor().ApplyChunkAsync(generation, Actor, later.OperationId!.Value, 100))).Status.Should().Be(409);
	}
}
