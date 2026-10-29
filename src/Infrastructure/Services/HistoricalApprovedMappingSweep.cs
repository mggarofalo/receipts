using System.Security.Cryptography;
using System.Text.Json;
using Application.Taxonomy;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

internal sealed record ApprovedMappingSweepRequest(Guid SweepId, string Actor, Guid TaxonomyRevisionId,
	Guid MappingRevisionId, HistoricalGroupKey Group, IReadOnlyList<Guid> ExcludedItemIds, Guid ExpectedRestoreGeneration);
internal sealed record ApprovedMappingSweepPage(Guid? OperationId, int Count, bool Completed);

// One invocation creates at most one frozen page. The caller applies it using the existing executor,
// then calls again. Capture, page identity and checkpoint commit together; no worker is activated here.
internal sealed class HistoricalApprovedMappingSweep(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock)
{
	internal async Task<ApprovedMappingSweepPage> NextAsync(ApprovedMappingSweepRequest request, CancellationToken ct = default)
	{
		if (request.SweepId == Guid.Empty || request.ExcludedItemIds == null)
		{ throw new TaxonomyException(400, "A sweep identity and exclusions are required."); }
		string intent = JsonSerializer.Serialize(new
		{
			request.Actor,
			request.TaxonomyRevisionId,
			request.MappingRevisionId,
			request.Group,
			ExcludedItemIds = request.ExcludedItemIds.Order().ToArray()
		});
		string intentHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(intent)));
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		await HistoricalRestoreGeneration.RequireCurrentAsync(db, request.ExpectedRestoreGeneration, ct);
		var sweeps = db.Set<HistoricalMappingSweepEntity>();
		var sweep = await sweeps.SingleOrDefaultAsync(x => x.Id == request.SweepId, ct);
		if (sweep == null)
		{
			sweep = new() { Id = request.SweepId, RequestHash = intentHash, RestoreGeneration = request.ExpectedRestoreGeneration };
			sweeps.Add(sweep);
		}
		else if (sweep.RestoreGeneration != request.ExpectedRestoreGeneration ||
				 sweep.RequestHash != intentHash)
		{ throw new TaxonomyException(409, "Sweep identity was already used with different intent or predates restore."); }
		if (sweep.Completed) { return new(null, 0, true); }
		if (sweep.PendingOperationId is Guid pending)
		{
			var operation = await db.HistoricalOperations.AsNoTracking().SingleAsync(x => x.Id == pending, ct);
			int count = await db.HistoricalOperationItems.CountAsync(x => x.OperationId == pending, ct);
			if (operation.State is HistoricalOperationState.Preview or HistoricalOperationState.Applying)
			{ return new(pending, count, false); }
			if (operation.State != HistoricalOperationState.Completed)
			{ throw new TaxonomyException(409, "Sweep page cannot continue in its current state."); }
			sweep.PendingOperationId = null;
		}
		Guid operationId = Guid.NewGuid();
		var capture = new ApprovedMappingCaptureRequest(operationId, request.Actor, request.TaxonomyRevisionId,
			request.MappingRevisionId, request.Group, request.ExcludedItemIds, request.ExpectedRestoreGeneration);
		// Historical operation rows are append-only, even while a transaction is open.
		// A savepoint lets an empty page roll back its provisional header while retaining
		// the sweep's completed checkpoint in the enclosing transaction.
		await tx.CreateSavepointAsync("capture_page", ct);
		int inserted = await new HistoricalApprovedMappingCapture(factory, clock)
			.CapturePageInTransactionAsync(db, capture, sweep.Cursor, true, ct);
		if (inserted == 0)
		{
			await tx.RollbackToSavepointAsync("capture_page", ct);
			db.ChangeTracker.Clear();
			sweep = await sweeps.SingleOrDefaultAsync(x => x.Id == request.SweepId, ct);
			if (sweep == null)
			{
				sweep = new() { Id = request.SweepId, RequestHash = intentHash, RestoreGeneration = request.ExpectedRestoreGeneration };
				sweeps.Add(sweep);
			}
			sweep.PendingOperationId = null;
			sweep.Completed = true;
		}
		else
		{
			sweep.Cursor = await db.HistoricalOperationItems.Where(x => x.OperationId == operationId)
				.OrderByDescending(x => x.ItemId).Select(x => x.ItemId).FirstAsync(ct);
			sweep.PendingOperationId = operationId;
		}
		await db.SaveChangesAsync(ct);
		await tx.CommitAsync(ct);
		return new(inserted == 0 ? null : operationId, inserted, inserted == 0);
	}
}
