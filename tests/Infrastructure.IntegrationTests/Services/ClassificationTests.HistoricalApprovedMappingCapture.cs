using Application.Normalization;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class ClassificationTests
{
	[Fact]
	public async Task ApprovedMappingCaptureFreezesEligibleEvidenceAndProtectsManualClear()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedMappingCaptureCore();
	}

	private async Task ApprovedMappingCaptureCore()
	{
		var seed = await HistoricalSeedAsync();
		var manual = await Service().AssignAsync(Actor, seed.Item,
			new(null, seed.Revision, seed.Assignment, Guid.NewGuid(), "Explicit clear"));
		var preview = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		var group = preview.Groups.Single().Group;
		await using var db = fixture.CreateDbContext();
		var original = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
		var items = Enumerable.Range(0, 2).Select(_ =>
		{
			var item = ReceiptItemEntityGenerator.Generate(original.ReceiptId);
			item.Description = original.Description; item.ReceiptItemCode = original.ReceiptItemCode;
			item.Category = ""; item.Subcategory = null; return item;
		}).ToList();
		await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync(items, default);
		var source = new NormalizationSourceKey(group.Store, group.RawDescription, group.ItemCode);
		var mappings = new CategoryMappingService(new Factory(fixture), TimeProvider.System);
		var mapping = await mappings.ChangeAsync(Actor, new(source, NormalizationCatalogPolicy.Hash(source), null,
			seed.Revision, seed.Root, Guid.NewGuid(), "Approve"));
		var capture = new HistoricalApprovedMappingCapture(new Factory(fixture), TimeProvider.System);
		var request = new ApprovedMappingCaptureRequest(Guid.NewGuid(), Actor, seed.Revision, mapping.Id, group, [items[1].Id], CurrentRestoreGeneration());
		(await capture.CaptureAsync(request)).Should().Be(1);
		var emptyRequest = request with { OperationId = Guid.NewGuid(), ExcludedItemIds = [items[0].Id, items[1].Id] };
		(await capture.CaptureAsync(emptyRequest)).Should().Be(0);
		var emptyHeader = await db.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == emptyRequest.OperationId);
		emptyHeader.ApprovedMappingRevisionId.Should().Be(mapping.Id);
		(await db.HistoricalOperationItems.AnyAsync(x => x.OperationId == emptyRequest.OperationId)).Should().BeFalse();
		await RejectHistoricalSql(db, "UPDATE taxonomy.\"HistoricalOperations\" SET \"ApprovedMappingRevisionId\" = NULL WHERE \"Id\" = {0}", emptyRequest.OperationId, "capture mapping is immutable");
		var deferred = await Historical().SetDispositionAsync(Actor,
			new(group, HistoricalGroupDisposition.Later, 0, Guid.NewGuid()));
		// A later review decision must not change the result of an already committed capture.
		(await capture.CaptureAsync(request)).Should().Be(1);
		await Historical().SetDispositionAsync(Actor, new(group, null, deferred.Version, Guid.NewGuid()));
		var row = await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == request.OperationId);
		var itemNow = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == items[0].Id);
		var receipt = await db.Receipts.AsNoTracking().SingleAsync(x => x.Id == itemNow.ReceiptId);
		var current = await db.CurrentClassifications.AsNoTracking().SingleAsync(x => x.ReceiptItemId == itemNow.Id);
		row.ItemId.Should().Be(items[0].Id); row.ApprovedMappingRevisionId.Should().Be(mapping.Id);
		row.ItemRevision.Should().Be(itemNow.NormalizationRevision); row.ReceiptRevision.Should().Be(receipt.NormalizationRevision);
		row.ExpectedAssignmentId.Should().Be(current.AssignmentId); row.TargetCategoryId.Should().Be(seed.Root);
		row.State.Should().Be(HistoricalItemState.Pending);
		(await db.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == request.OperationId)).Kind.Should().Be(HistoricalOperationKind.ApprovedMappingV1);
		(await Service().HistoryAsync(seed.Item, null, 1)).Items.Single().Id.Should().Be(manual.Id);
		(await Assert.ThrowsAsync<TaxonomyException>(() => capture.CaptureAsync(request with { ExcludedItemIds = [] }))).Status.Should().Be(409);
		var manualPreview = await HistoricalRequest(seed.Item, seed.Revision, seed.Root);
		await Historical().SavePreviewAsync(manualPreview);
		var listed = await Historical().ListAsync(Actor, 0, 25);
		listed.Items.Select(x => x.OperationId).Should().Contain(manualPreview.OperationId).And.NotContain(request.OperationId);
		(await Historical().ReadSummaryAsync(Actor, manualPreview.OperationId)).OperationId.Should().Be(manualPreview.OperationId);
		(await Assert.ThrowsAsync<TaxonomyException>(() => Historical().ReadSummaryAsync(Actor, request.OperationId))).Status.Should().Be(404);
		(await Assert.ThrowsAsync<TaxonomyException>(() => Historical().ReadAsync(Actor, request.OperationId))).Status.Should().Be(404);
		(await Assert.ThrowsAsync<TaxonomyException>(() => Historical().ReadItemsAsync(Actor, request.OperationId, 0, 10))).Status.Should().Be(404);
		(await Assert.ThrowsAsync<TaxonomyException>(() => Historical().CancelAsync(Actor, request.OperationId))).Status.Should().Be(409);
		(await Assert.ThrowsAsync<TaxonomyException>(() => Historical().ApplyChunkAsync(Actor, request.OperationId, 1))).Status.Should().Be(409);
		await Assert.ThrowsAsync<InvalidDataException>(() => AcceptanceExport());
		await mappings.ChangeAsync(Actor, new(source, NormalizationCatalogPolicy.Hash(source), mapping.Id,
			seed.Revision, null, Guid.NewGuid(), "Revoke"));
		(await Assert.ThrowsAsync<TaxonomyException>(() => capture.CaptureAsync(request with { OperationId = Guid.NewGuid() }))).Status.Should().Be(409);
	}
	[Fact]
	public async Task ApprovedMappingCaptureRespectsActorPrivateDispositionsAndTheirLock()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedMappingDispositionCore();
	}

	private async Task ApprovedMappingDispositionCore()
	{
		var seed = await HistoricalSeedAsync();
		var group = (await HistoricalRequest(seed.Item, seed.Revision, seed.Root)).Groups.Single().Group;
		var source = new NormalizationSourceKey(group.Store, group.RawDescription, group.ItemCode);
		var mapping = await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor,
			new(source, NormalizationCatalogPolicy.Hash(source), null, seed.Revision, seed.Root, Guid.NewGuid(), "Approve"));
		var capture = new HistoricalApprovedMappingCapture(new Factory(fixture), TimeProvider.System);
		var request = new ApprovedMappingCaptureRequest(Guid.NewGuid(), Actor, seed.Revision, mapping.Id, group, [], CurrentRestoreGeneration());
		var later = await Historical().SetDispositionAsync(Actor, new(group, HistoricalGroupDisposition.Later, 0, Guid.NewGuid()));
		(await Assert.ThrowsAsync<TaxonomyException>(() => capture.CaptureAsync(request))).Status.Should().Be(409);
		// Another actor's review state is not global suppression.
		(await capture.CaptureAsync(request with { OperationId = Guid.NewGuid(), Actor = "other actor" })).Should().BeGreaterThanOrEqualTo(0);
		var leave = await Historical().SetDispositionAsync(Actor, new(group, HistoricalGroupDisposition.LeaveAsIs, later.Version, Guid.NewGuid()));
		(await Assert.ThrowsAsync<TaxonomyException>(() => capture.CaptureAsync(request))).Status.Should().Be(409);
		var cleared = await Historical().SetDispositionAsync(Actor, new(group, null, leave.Version, Guid.NewGuid()));
		// Hold the exact source lock while a disposition changes. Capture must wait and observe the committed change.
		await using var db = fixture.CreateDbContext();
		await using var tx = await db.Database.BeginTransactionAsync();
		string hash = HistoricalReclassificationService.DispositionSourceHash(group);
		await HistoricalReclassificationService.LockDispositionSourceAsync(db, Actor, hash, default);
		Task<int> pending = capture.CaptureAsync(request);
		int waiters = 0;
		for (int attempt = 0; attempt < 100 && waiters == 0 && !pending.IsCompleted; attempt++)
		{
			waiters = await db.Database.SqlQueryRaw<int>(
				"SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted").SingleAsync();
			if (waiters == 0) { await Task.Delay(50); }
		}
		waiters.Should().BeGreaterThan(0, "capture must wait for the held actor/source advisory lock");
		pending.IsCompleted.Should().BeFalse();
		var row = await db.HistoricalGroupDispositions.SingleAsync(x => x.Actor == Actor && x.SourceHash == hash);
		row.Disposition = HistoricalGroupDisposition.Later; row.Version = cleared.Version + 1;
		row.OperationId = Guid.NewGuid(); row.UpdatedAt = DateTimeOffset.UtcNow;
		await db.SaveChangesAsync();
		await tx.CommitAsync();
		(await Assert.ThrowsAsync<TaxonomyException>(async () => await pending)).Status.Should().Be(409);
		(await db.HistoricalOperations.AnyAsync(x => x.Id == request.OperationId)).Should().BeFalse();
	}

}
