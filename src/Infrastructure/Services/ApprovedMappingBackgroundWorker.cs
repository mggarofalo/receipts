using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Taxonomy;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

// Register as a singleton hosted service only with the activity/undo surface.
// Each pass captures/applies at most 100 purchases. No provider or consent dependency.
public sealed class ApprovedMappingBackgroundWorker(
	IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock,
	IOptions<ClassificationOptions> options, ILogger<ApprovedMappingBackgroundWorker> logger,
	ICommittedChangePublisher? publisher = null) : BackgroundService
{
	// Vary the first source across restarts; subsequent passes rotate deterministically.
	private Guid? mappingCursor = Guid.NewGuid();
	private sealed record Selection(HistoricalGroupKey Group, Guid TargetCategoryId, Guid[] ExcludedItemIds);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				using var pass = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
				pass.CancelAfter(TimeSpan.FromSeconds(15));
				await RunOnceAsync(pass.Token);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
			catch (Exception exception)
			{
				logger.LogWarning("Approved mapping background pass deferred ({ExceptionType})", exception.GetType().Name);
			}
			try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
		}
	}

	public async Task RunOnceAsync(CancellationToken ct = default)
	{
		if (!options.Value.WritesEnabled) { return; }
		await using var db = await factory.CreateDbContextAsync(ct);
		Guid generation = await db.CategoryAssistanceSettings.AsNoTracking().Select(x => x.RestoreGeneration).SingleAsync(ct);
		var pendingQuery = from sweep in db.Set<HistoricalMappingSweepEntity>().AsNoTracking()
						   join operation in db.HistoricalOperations.AsNoTracking() on sweep.PendingOperationId equals operation.Id
						   where !sweep.Completed && sweep.RestoreGeneration == generation
						   select new { Sweep = sweep, Operation = operation };
		var sources = (from head in db.CategoryMappingHeads.AsNoTracking()
					   join revision in db.CategoryMappingRevisions.AsNoTracking() on head.RevisionId equals revision.Id
					   where revision.CategoryId != null
					   select revision.Id)
			.Union(pendingQuery.Select(x => x.Operation.ApprovedMappingRevisionId!.Value));
		Guid? mappingId = await sources.Where(id => !mappingCursor.HasValue || id.CompareTo(mappingCursor.Value) > 0)
			.OrderBy(id => id).Select(id => (Guid?)id).FirstOrDefaultAsync(ct);
		mappingId ??= await sources.OrderBy(id => id).Select(id => (Guid?)id).FirstOrDefaultAsync(ct);
		if (mappingId == null) { mappingCursor = null; return; }
		// Rotate before any capture/apply: even a failed or timed-out source yields its turn.
		mappingCursor = mappingId;
		var pending = await pendingQuery.Where(x => x.Operation.ApprovedMappingRevisionId == mappingId)
			.OrderBy(x => x.Operation.CapturedAt).ThenBy(x => x.Operation.Id).FirstOrDefaultAsync(ct);
		ApprovedMappingSweepRequest request;
		if (pending != null)
		{
			var operation = pending.Operation;
			if (operation.State is HistoricalOperationState.Cancelled or HistoricalOperationState.Undoing or HistoricalOperationState.Undone)
			{
				await FinishAsync(pending.Sweep.Id, generation, ct);
				return;
			}
			var selection = JsonSerializer.Deserialize<Selection[]>(operation.SelectionJson)!.Single();
			request = new(pending.Sweep.Id, operation.Actor, operation.TaxonomyRevisionId,
				operation.ApprovedMappingRevisionId!.Value, selection.Group, selection.ExcludedItemIds, pending.Sweep.RestoreGeneration);
			// Apply the frozen page before capturing another; this also resumes a crash after capture.
			if (operation.State is HistoricalOperationState.Preview or HistoricalOperationState.Applying)
			{
				await ApplyAsync(request, operation.Id, ct);
				return;
			}
		}
		else
		{
			var mapping = await db.CategoryMappingRevisions.AsNoTracking().SingleAsync(x => x.Id == mappingId, ct);
			Guid taxonomy = await db.TaxonomyHeads.AsNoTracking().Select(x => x.RevisionId).SingleAsync(ct);
			request = new(Guid.NewGuid(), mapping.Actor, taxonomy, mapping.Id,
				new(mapping.Location, mapping.Description, mapping.ItemCode), [], generation);
		}
		ApprovedMappingSweepPage page;
		try { page = await new HistoricalApprovedMappingSweep(factory, clock).NextAsync(request, ct); }
		catch (TaxonomyException error) when (error.Status == 409)
		{
			// Revocation, taxonomy edits and deferrals retire this scan; the rotating
			// head scan can reconsider current intent without replaying stale authority.
			await FinishAsync(request.SweepId, request.ExpectedRestoreGeneration, ct);
			return;
		}
		if (page.OperationId is Guid id) { await ApplyAsync(request, id, ct); }
	}

	private async Task ApplyAsync(ApprovedMappingSweepRequest request, Guid operationId, CancellationToken ct)
	{
		var progress = await new HistoricalApprovedMappingExecutor(factory, clock, options)
			.ApplyChunkAsync(request.ExpectedRestoreGeneration, request.Actor, operationId, 100, ct);
		if (progress.Applied > 0 && publisher != null)
		{
			await publisher.PublishAsync(new(CommittedEntityType.ReceiptItem, CommittedChangeType.Updated, SuppressToast: true));
		}
	}

	private async Task FinishAsync(Guid sweepId, Guid generation, CancellationToken ct)
	{
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		await HistoricalRestoreGeneration.RequireCurrentAsync(db, generation, ct);
		var sweep = await db.Set<HistoricalMappingSweepEntity>().SingleOrDefaultAsync(x => x.Id == sweepId, ct);
		if (sweep != null) { sweep.Completed = true; await db.SaveChangesAsync(ct); }
		await tx.CommitAsync(ct);
	}
}
