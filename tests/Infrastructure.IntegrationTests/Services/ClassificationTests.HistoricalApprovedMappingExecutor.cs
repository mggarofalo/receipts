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
	private HistoricalApprovedMappingExecutor ApprovedExecutor() => new(new Factory(fixture), TimeProvider.System,
		Options.Create(new ClassificationOptions { WritesEnabled = true }));

	private async Task<(ApprovedMappingCaptureRequest Request, Guid Item, Guid Assignment, Guid Category)> CaptureExecutableAsync()
	{
		var seed = await HistoricalSeedAsync();
		var group = (await HistoricalRequest(seed.Item, seed.Revision, seed.Root)).Groups.Single().Group;
		await using var db = fixture.CreateDbContext();
		var original = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == seed.Item);
		var item = ReceiptItemEntityGenerator.Generate(original.ReceiptId);
		item.Description = original.Description; item.ReceiptItemCode = original.ReceiptItemCode;
		item.Category = ""; item.Subcategory = null;
		await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync([item], default);
		var assignment = (await Service().HistoryAsync(item.Id, null, 1)).Items.Single().Id;
		var source = new NormalizationSourceKey(group.Store, group.RawDescription, group.ItemCode);
		var mapping = await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor,
			new(source, NormalizationCatalogPolicy.Hash(source), null, seed.Revision, seed.Root, Guid.NewGuid(), "Approve"));
		var request = new ApprovedMappingCaptureRequest(Guid.NewGuid(), Actor, seed.Revision, mapping.Id, group, []);
		(await new HistoricalApprovedMappingCapture(new Factory(fixture), TimeProvider.System).CaptureAsync(request)).Should().Be(1);
		return (request, item.Id, assignment, seed.Root);
	}

	[Fact]
	public async Task ApprovedExecutorAppliesAtomicallyAndReplaysWithoutAnotherAssignment()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedExecutorReplayCore();
	}
	private async Task ApprovedExecutorReplayCore()
	{
		var captured = await CaptureExecutableAsync();
		var executor = ApprovedExecutor();
		var results = await Task.WhenAll(executor.ApplyChunkAsync(Actor, captured.Request.OperationId, 1),
			executor.ApplyChunkAsync(Actor, captured.Request.OperationId, 1));
		results.Should().OnlyContain(x => x.Applied == 1 && x.Pending == 0 && x.State == HistoricalOperationState.Completed);
		var history = await Service().HistoryAsync(captured.Item, null, 10);
		history.Items.Should().HaveCount(2);
		history.Items.First().Source.Should().Be("approved_mapping");
		history.Items.First().CategoryId.Should().Be(captured.Category);
		await using var db = fixture.CreateDbContext();
		var row = await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == captured.Request.OperationId);
		row.AppliedAssignmentId.Should().Be(history.Items.First().Id);
	}

	[Fact]
	public async Task ApprovedExecutorPreservesManualClearAndRevokedMapping()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedExecutorProtectionCore();
	}
	private async Task ApprovedExecutorProtectionCore()
	{
		var first = await CaptureExecutableAsync();
		var correction = await Service().AssignAsync(Actor, first.Item,
			new(null, first.Request.TaxonomyRevisionId, first.Assignment, Guid.NewGuid(), "Explicit clear"));
		var skipped = await ApprovedExecutor().ApplyChunkAsync(Actor, first.Request.OperationId, 10);
		skipped.Skipped.Should().Be(1);
		(await Service().HistoryAsync(first.Item, null, 1)).Items.Single().Id.Should().Be(correction.Id);
		var second = await CaptureExecutableAsync();
		var source = new NormalizationSourceKey(second.Request.Group.Store, second.Request.Group.RawDescription, second.Request.Group.ItemCode);
		await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor,
			new(source, NormalizationCatalogPolicy.Hash(source), second.Request.MappingRevisionId,
				second.Request.TaxonomyRevisionId, null, Guid.NewGuid(), "Revoke"));
		(await ApprovedExecutor().ApplyChunkAsync(Actor, second.Request.OperationId, 10)).Skipped.Should().Be(1);
		(await Service().HistoryAsync(second.Item, null, 10)).Items.Should().ContainSingle();
	}

	[Fact]
	public async Task ApprovedExecutorWaitsForDispositionAndObservesCommittedDeferral()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedExecutorDispositionCore();
	}
	private async Task ApprovedExecutorDispositionCore()
	{
		var captured = await CaptureExecutableAsync();
		var group = captured.Request.Group;
		await using var db = fixture.CreateDbContext();
		await using var tx = await db.Database.BeginTransactionAsync();
		var hash = HistoricalReclassificationService.DispositionSourceHash(group);
		await HistoricalReclassificationService.LockDispositionSourceAsync(db, Actor, hash, default);
		var pending = ApprovedExecutor().ApplyChunkAsync(Actor, captured.Request.OperationId, 1);
		int waiters = 0;
		for (int i = 0; i < 100 && waiters == 0 && !pending.IsCompleted; i++)
		{
			waiters = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted").SingleAsync();
			if (waiters == 0) { await Task.Delay(50); }
		}
		waiters.Should().BeGreaterThan(0);
		db.HistoricalGroupDispositions.Add(new()
		{
			Id = Guid.NewGuid(),
			Actor = Actor,
			SourceHash = hash,
			Store = group.Store,
			RawDescription = group.RawDescription,
			ItemCode = group.ItemCode,
			Disposition = HistoricalGroupDisposition.Later,
			Version = 1,
			OperationId = Guid.NewGuid(),
			UpdatedAt = DateTimeOffset.UtcNow
		});
		await db.SaveChangesAsync();
		await tx.CommitAsync();
		(await pending).Skipped.Should().Be(1);
		(await Service().HistoryAsync(captured.Item, null, 10)).Items.Should().ContainSingle();
	}
	[Fact]
	public async Task ApprovedExecutorHonorsCurrentExclusionsAndRollsBackWhenWritesDisabled()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedExecutorPreferenceAndRollbackCore();
	}
	private async Task ApprovedExecutorPreferenceAndRollbackCore()
	{
		var first = await CaptureExecutableAsync();
		await using (var db = fixture.CreateDbContext())
		{
			var attempt = new Infrastructure.Entities.CategoryAssistance.CategoryAssistanceAttemptEntity
			{
				Id = Guid.NewGuid(),
				RequestId = Guid.NewGuid(),
				RequestScope = Guid.NewGuid(),
				SourceHash = "synthetic",
				Location = first.Request.Group.Store,
				Description = first.Request.Group.RawDescription,
				ItemCodeKey = first.Request.Group.ItemCode ?? "",
				State = "suggested"
			};
			db.CategoryAssistanceAttempts.Add(attempt);
			db.CategoryAssistanceReviewPreferences.Add(new()
			{
				AttemptId = attempt.Id,
				Actor = Actor,
				State = "active",
				ExcludedItemIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { first.Item }),
				Revision = Guid.NewGuid()
			});
			await db.SaveChangesAsync();
		}
		(await ApprovedExecutor().ApplyChunkAsync(Actor, first.Request.OperationId, 1)).Skipped.Should().Be(1);
		(await Service().HistoryAsync(first.Item, null, 10)).Items.Should().ContainSingle();
		var second = await CaptureExecutableAsync();
		var disabled = new HistoricalApprovedMappingExecutor(new Factory(fixture), TimeProvider.System,
			Options.Create(new ClassificationOptions { WritesEnabled = false }));
		await Assert.ThrowsAsync<TaxonomyException>(() => disabled.ApplyChunkAsync(Actor, second.Request.OperationId, 1));
		await using var verify = fixture.CreateDbContext();
		(await verify.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == second.Request.OperationId)).State.Should().Be(HistoricalOperationState.Preview);
		var row = await verify.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == second.Request.OperationId);
		row.State.Should().Be(HistoricalItemState.Pending);
		row.AppliedAssignmentId.Should().BeNull();
		(await Service().HistoryAsync(second.Item, null, 10)).Items.Should().ContainSingle();
	}

	[Fact]
	public async Task ApprovedExecutorObservesRevocationWhileWaitingForWriteGate()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedExecutorRevocationRaceCore();
	}
	private async Task ApprovedExecutorRevocationRaceCore()
	{
		var captured = await CaptureExecutableAsync();
		await using var db = fixture.CreateDbContext();
		await using var tx = await db.Database.BeginTransactionAsync();
		await Infrastructure.Utilities.NormalizationWriteGuard.AcquireWriteGateAsync(db, default);
		var pending = ApprovedExecutor().ApplyChunkAsync(Actor, captured.Request.OperationId, 1);
		int waiters = 0;
		for (int i = 0; i < 100 && waiters == 0 && !pending.IsCompleted; i++)
		{
			waiters = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted").SingleAsync();
			if (waiters == 0) { await Task.Delay(50); }
		}
		waiters.Should().BeGreaterThan(0);
		var group = captured.Request.Group;
		var source = new NormalizationSourceKey(group.Store, group.RawDescription, group.ItemCode);
		await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor,
			new(source, NormalizationCatalogPolicy.Hash(source), captured.Request.MappingRevisionId,
				captured.Request.TaxonomyRevisionId, null, Guid.NewGuid(), "Revoke while execution waits"));
		await tx.CommitAsync();
		(await pending).Skipped.Should().Be(1);
		(await Service().HistoryAsync(captured.Item, null, 10)).Items.Should().ContainSingle();
	}

	[Theory]
	[InlineData("null")]
	[InlineData("not-json")]
	[InlineData("[\"00000000-0000-0000-0000-000000000000\"]")]
	[InlineData("[\"bad-guid\"]")]
	public async Task ApprovedExecutorFailsClosedOnMalformedPreference(string payload)
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedExecutorMalformedPreferenceCore(payload);
	}
	private async Task ApprovedExecutorMalformedPreferenceCore(string payload)
	{
		var captured = await CaptureExecutableAsync();
		await using (var db = fixture.CreateDbContext())
		{
			var group = captured.Request.Group;
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
				Actor = Actor,
				State = "active",
				ExcludedItemIdsJson = payload,
				Revision = Guid.NewGuid()
			});
			await db.SaveChangesAsync();
		}
		(await ApprovedExecutor().ApplyChunkAsync(Actor, captured.Request.OperationId, 1)).Skipped.Should().Be(1);
		(await Service().HistoryAsync(captured.Item, null, 10)).Items.Should().ContainSingle();
	}

	[Fact]
	public async Task ApprovedExecutorRejectsItemsOutsideFrozenCaptureScope()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedExecutorScopeCore();
	}
	private async Task ApprovedExecutorScopeCore()
	{
		var captured = await CaptureExecutableAsync();
		await using var db = fixture.CreateDbContext();
		var original = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == captured.Item);
		var other = ReceiptItemEntityGenerator.Generate(original.ReceiptId);
		other.Description = "Other approved source " + Guid.NewGuid().ToString("N");
		other.ReceiptItemCode = original.ReceiptItemCode; other.Category = ""; other.Subcategory = null;
		await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync([other], default);
		var otherGroup = (await HistoricalRequest(other.Id, captured.Request.TaxonomyRevisionId, captured.Category)).Groups.Single().Group;
		var source = new NormalizationSourceKey(otherGroup.Store, otherGroup.RawDescription, otherGroup.ItemCode);
		var mapping = await new CategoryMappingService(new Factory(fixture), TimeProvider.System).ChangeAsync(Actor,
			new(source, NormalizationCatalogPolicy.Hash(source), null, captured.Request.TaxonomyRevisionId,
				captured.Category, Guid.NewGuid(), "Approve other"));
		var otherRequest = new ApprovedMappingCaptureRequest(Guid.NewGuid(), Actor, captured.Request.TaxonomyRevisionId, mapping.Id, otherGroup, []);
		(await new HistoricalApprovedMappingCapture(new Factory(fixture), TimeProvider.System).CaptureAsync(otherRequest)).Should().Be(1);
		var injected = await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == otherRequest.OperationId);
		injected.OperationId = captured.Request.OperationId; injected.ApplyOperationId = Guid.NewGuid(); injected.UndoOperationId = Guid.NewGuid();
		db.HistoricalOperationItems.Add(injected); await db.SaveChangesAsync();
		var progress = await ApprovedExecutor().ApplyChunkAsync(Actor, captured.Request.OperationId, 10);
		progress.Applied.Should().Be(1); progress.Skipped.Should().Be(1);
		(await Service().HistoryAsync(other.Id, null, 10)).Items.Should().ContainSingle();
		(await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == captured.Request.OperationId && x.ItemId == other.Id))
			.Reason.Should().Be("invalid-capture-evidence");
	}

	[Fact]
	public async Task ApprovedExecutorWaitsForConcurrentManualClear()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedExecutorCorrectionRaceCore();
	}
	private async Task ApprovedExecutorCorrectionRaceCore()
	{
		var captured = await CaptureExecutableAsync();
		var winner = new HistoricalRaceGate(true); var loser = new HistoricalRaceGate(false);
		var correctionService = new ClassificationService(new HistoricalRaceFactory(fixture, winner), TimeProvider.System,
			Options.Create(new ClassificationOptions { WritesEnabled = true }));
		var executor = new HistoricalApprovedMappingExecutor(new HistoricalRaceFactory(fixture, loser), TimeProvider.System,
			Options.Create(new ClassificationOptions { WritesEnabled = true }));
		var correction = correctionService.AssignAsync(Actor, captured.Item,
			new(null, captured.Request.TaxonomyRevisionId, captured.Assignment, Guid.NewGuid(), "Concurrent clear"));
		Task<HistoricalProgress>? pending = null;
		try
		{
			await winner.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
			pending = executor.ApplyChunkAsync(Actor, captured.Request.OperationId, 1);
			await AssertHistoricalDatabaseWait(winner, loser);
		}
		finally { winner.Release.TrySetResult(); }
		var manual = await correction;
		(await pending!).Skipped.Should().Be(1);
		(await Service().HistoryAsync(captured.Item, null, 1)).Items.Single().Id.Should().Be(manual.Id);
	}

	[Fact]
	public async Task ApprovedExecutorRollsBackMidChunkAndRetries()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedExecutorMidChunkCore();
	}
	private async Task ApprovedExecutorMidChunkCore()
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
		var failing = new HistoricalApprovedMappingExecutor(new FailHistoricalFactory(fixture), TimeProvider.System,
			Options.Create(new ClassificationOptions { WritesEnabled = true }));
		await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ApplyChunkAsync(Actor, request.OperationId, 2));
		(await db.HistoricalOperationItems.AsNoTracking().Where(x => x.OperationId == request.OperationId).ToListAsync())
			.Should().OnlyContain(x => x.State == HistoricalItemState.Pending && x.AppliedAssignmentId == null);
		(await Service().HistoryAsync(captured.Item, null, 10)).Items.Should().ContainSingle();
		(await Service().HistoryAsync(sibling.Id, null, 10)).Items.Should().ContainSingle();
		(await ApprovedExecutor().ApplyChunkAsync(Actor, request.OperationId, 2)).Applied.Should().Be(2);
	}

	[Fact]
	public async Task ApprovedExecutorRejectsInjectedExcludedItem()
	{
		await using var isolated = await fixture.CreateIsolatedAsync();
		await new ClassificationTests(isolated).ApprovedExecutorExcludedScopeCore();
	}
	private async Task ApprovedExecutorExcludedScopeCore()
	{
		var captured = await CaptureExecutableAsync();
		await using var db = fixture.CreateDbContext();
		var original = await db.ReceiptItems.AsNoTracking().SingleAsync(x => x.Id == captured.Item);
		var sibling = ReceiptItemEntityGenerator.Generate(original.ReceiptId);
		sibling.Description = original.Description; sibling.ReceiptItemCode = original.ReceiptItemCode;
		sibling.Category = ""; sibling.Subcategory = null;
		await new Infrastructure.Repositories.ReceiptItemRepository(new ActorFactory(fixture)).CreateAsync([sibling], default);
		var request = captured.Request with { OperationId = Guid.NewGuid(), ExcludedItemIds = [captured.Item] };
		(await new HistoricalApprovedMappingCapture(new Factory(fixture), TimeProvider.System).CaptureAsync(request)).Should().Be(1);
		var injected = await db.HistoricalOperationItems.AsNoTracking().SingleAsync(x => x.OperationId == captured.Request.OperationId);
		injected.OperationId = request.OperationId; injected.ApplyOperationId = Guid.NewGuid(); injected.UndoOperationId = Guid.NewGuid();
		db.HistoricalOperationItems.Add(injected); await db.SaveChangesAsync();
		var progress = await ApprovedExecutor().ApplyChunkAsync(Actor, request.OperationId, 10);
		progress.Applied.Should().Be(1); progress.Skipped.Should().Be(1);
		(await Service().HistoryAsync(captured.Item, null, 10)).Items.Should().ContainSingle();
		(await Service().HistoryAsync(sibling.Id, null, 10)).Items.Should().HaveCount(2);
	}

}
