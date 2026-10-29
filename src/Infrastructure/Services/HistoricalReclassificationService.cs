using System.Security.Cryptography;
using System.Text.Json;
using Application.Taxonomy;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public sealed class HistoricalReclassificationService(IDbContextFactory<ApplicationDbContext> factory,
		TimeProvider clock, IOptions<ClassificationOptions> options) : IHistoricalOperationService
{
	public async Task<HistoricalOperationPage> ListAutomaticAsync(string actor, int offset, int limit, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(actor) || offset < 0 || limit is < 1 or > 25)
		{ throw new ArgumentException("Actor, nonnegative offset and limit of 1 to 25 are required."); }
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		List<HistoricalOperationEntity> rows = await db.HistoricalOperations.AsNoTracking()
			.Where(x => x.Actor == actor && x.Kind == HistoricalOperationKind.ApprovedMappingV1)
			.OrderByDescending(x => x.CapturedAt).ThenByDescending(x => x.Id)
			.Skip(offset).Take(limit + 1).ToListAsync(ct);
		List<HistoricalOperationSummary> summaries = [];
		foreach (HistoricalOperationEntity row in rows.Take(limit))
		{ summaries.Add(await SummaryAsync(db, row, ct)); }
		return new(summaries, rows.Count > limit ? checked(offset + limit) : null);
	}

	public async Task<HistoricalOperationPage> ListAsync(string actor, int offset, int limit, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(actor) || offset < 0 || limit is < 1 or > 25)
		{ throw new ArgumentException("Actor, nonnegative offset and limit of 1 to 25 are required."); }
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		List<HistoricalOperationEntity> rows = await db.HistoricalOperations.AsNoTracking()
				.Where(x => x.Actor == actor && x.Kind == HistoricalOperationKind.ManualV1).OrderByDescending(x => x.CapturedAt).ThenByDescending(x => x.Id)
				.Skip(offset).Take(limit + 1).ToListAsync(ct);
		List<HistoricalOperationSummary> summaries = [];
		foreach (HistoricalOperationEntity row in rows.Take(limit))
		{
			summaries.Add(await SummaryAsync(db, row, ct));
		}
		return new(summaries, rows.Count > limit ? checked(offset + limit) : null);
	}

	public async Task<HistoricalDispositionRecord> SetDispositionAsync(string actor,
			HistoricalDispositionRequest request, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(actor) || request.Group == null ||
				string.IsNullOrWhiteSpace(request.Group.Store) || string.IsNullOrWhiteSpace(request.Group.RawDescription) ||
				request.Group.Store.Contains('\0') || request.Group.RawDescription.Contains('\0') ||
				request.Group.ItemCode?.Contains('\0') == true || request.ExpectedVersion < 0 ||
				request.OperationId == Guid.Empty ||
				request.Disposition is not (null or HistoricalGroupDisposition.Later or HistoricalGroupDisposition.LeaveAsIs))
		{ throw new TaxonomyException(400, "Valid exact source, disposition, version and operation ID are required."); }
		string hash = DispositionSourceHash(request.Group);
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		await using var transaction = await db.Database.BeginTransactionAsync(ct);
		// Restore and historical execution take the same gate before inspecting review state.
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		// Operation ID precedes source in the lock order, including when the ID is reused across sources.
		await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"historical-disposition-operation:" + request.OperationId.ToString("D")}, 0))", ct);
		HistoricalDispositionOperationEntity? previous = await db.HistoricalDispositionOperations.AsNoTracking()
				.SingleOrDefaultAsync(x => x.OperationId == request.OperationId, ct);
		if (previous != null)
		{
			if (previous.Actor != actor || previous.SourceHash != hash ||
					previous.Store != request.Group.Store ||
					previous.RawDescription != request.Group.RawDescription ||
					previous.ItemCode != request.Group.ItemCode ||
					previous.Disposition != request.Disposition ||
					previous.ExpectedVersion != request.ExpectedVersion)
			{ throw new TaxonomyException(409, "Operation ID was already used with a different request."); }
			return new(new(previous.Store, previous.RawDescription, previous.ItemCode),
					previous.Disposition, previous.ResultVersion, previous.OperationId,
					previous.ResultUpdatedAt);
		}
		// Serialize first insert and updates for this actor/source after taking the write gate.
		await LockDispositionSourceAsync(db, actor, hash, ct);
		HistoricalGroupDispositionEntity? row = await db.HistoricalGroupDispositions
				.SingleOrDefaultAsync(x => x.Actor == actor && x.SourceHash == hash, ct);
		if (row != null && (row.Store != request.Group.Store ||
				row.RawDescription != request.Group.RawDescription || row.ItemCode != request.Group.ItemCode))
		{ throw new TaxonomyException(409, "Source hash collision."); }
		if ((row?.Version ?? 0) != request.ExpectedVersion)
		{ throw new TaxonomyException(409, "Disposition changed. Refresh before updating."); }
		if (row == null)
		{
			row = new HistoricalGroupDispositionEntity
			{
				Id = Guid.NewGuid(),
				Actor = actor,
				SourceHash = hash,
				Store = request.Group.Store,
				RawDescription = request.Group.RawDescription,
				ItemCode = request.Group.ItemCode
			};
			db.HistoricalGroupDispositions.Add(row);
		}
		row.Disposition = request.Disposition;
		row.Version++;
		row.OperationId = request.OperationId;
		DateTimeOffset now = clock.GetUtcNow();
		row.UpdatedAt = now.AddTicks(-(now.Ticks % 10));
		db.HistoricalDispositionOperations.Add(new HistoricalDispositionOperationEntity
		{
			OperationId = request.OperationId,
			Actor = actor,
			SourceHash = hash,
			Store = request.Group.Store,
			RawDescription = request.Group.RawDescription,
			ItemCode = request.Group.ItemCode,
			Disposition = request.Disposition,
			ExpectedVersion = request.ExpectedVersion,
			ResultVersion = row.Version,
			ResultUpdatedAt = row.UpdatedAt
		});
		await db.SaveChangesAsync(ct);
		await transaction.CommitAsync(ct);
		return ToRecord(row);
	}

	internal static string DispositionSourceHash(HistoricalGroupKey group) =>
		Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(group)));

	internal static Task<int> LockDispositionSourceAsync(ApplicationDbContext db, string actor, string hash, CancellationToken ct) =>
		db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({actor + hash}, 0))", ct);

	private static HistoricalDispositionRecord ToRecord(HistoricalGroupDispositionEntity row) =>
			new(new(row.Store, row.RawDescription, row.ItemCode), row.Disposition,
					row.Version, row.OperationId, row.UpdatedAt);

	public async Task<HistoricalProgress> SavePreviewAsync(HistoricalPreviewRequest request, CancellationToken ct = default)
	{
		if (request.OperationId == Guid.Empty || request.TaxonomyRevisionId == Guid.Empty ||
			string.IsNullOrWhiteSpace(request.Actor) || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Contains('\0'))
		{ throw new TaxonomyException(400, "Actor, operation ID, taxonomy revision and reason are required."); }
		// Freeze all caller-owned collections before awaiting. Canonical order makes reordered selection replay equivalent.
		HistoricalGroupSelection[] groups = request.Groups.OrderBy(x => x.Group.Store, StringComparer.Ordinal)
			.ThenBy(x => x.Group.RawDescription, StringComparer.Ordinal)
			.ThenBy(x => x.Group.ItemCode, StringComparer.Ordinal).ToArray();
		HistoricalReclassificationPolicy.ValidateSelection(groups.Select(x => x.Group).ToArray(), []);
		if (groups.Any(x => x.Group.Store.Contains('\0') || x.Group.RawDescription.Contains('\0') || x.Group.ItemCode?.Contains('\0') == true))
		{ throw new TaxonomyException(400, "Selected source strings cannot contain NUL."); }
		string json = JsonSerializer.Serialize(groups);
		string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
		{ request.Actor, request.TaxonomyRevisionId, request.Reason, Groups = groups })));
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		await using var transaction = await db.Database.BeginTransactionAsync(ct);
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		HistoricalOperationEntity? existing = await db.HistoricalOperations.SingleOrDefaultAsync(x => x.Id == request.OperationId, ct);
		if (existing != null)
		{
			if (existing.Kind != HistoricalOperationKind.ManualV1 || existing.Actor != request.Actor || existing.RequestHash != hash)
			{ throw new TaxonomyException(409, "Operation ID was already used with a different request."); }
			return await ProgressAsync(db, existing, ct);
		}
		TaxonomyHeadEntity head = await HeadAsync(db, ct);
		if (head.RevisionId != request.TaxonomyRevisionId) { throw new TaxonomyException(409, "Taxonomy changed. Refresh before previewing."); }
		foreach (HistoricalGroupSelection selection in groups)
		{
			if (selection.TargetCategoryId is Guid target && !await db.TaxonomyNodes.AnyAsync(x =>
				x.RevisionId == head.RevisionId && x.CategoryId == target && !x.Archived, ct))
			{ throw new TaxonomyException(400, "Target category must be selectable."); }
		}
		HistoricalOperationEntity operation = new()
		{
			Id = request.OperationId,
			Actor = request.Actor,
			TaxonomyRevisionId = head.RevisionId,
			Reason = request.Reason,
			RequestHash = hash,
			SelectionJson = json,
			CapturedAt = clock.GetUtcNow(),
			State = HistoricalOperationState.Preview
		};
		db.HistoricalOperations.Add(operation);
		await db.SaveChangesAsync(ct);
		// INSERT SELECT materializes all exact selected sources in SQL, without an unbounded client manifest.
		// The canonical write gate holds source/assignment/taxonomy evidence stable throughout capture.
		foreach (HistoricalGroupSelection selection in groups)
		{
			await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO taxonomy."HistoricalOperationItems"
                ("OperationId", "ItemId", "ReceiptId", "Store", "RawDescription", "ItemCode",
                 "ItemRevision", "ReceiptRevision", "ExpectedAssignmentId", "PreviousCategoryId", "TargetCategoryId",
                 "ApplyOperationId", "UndoOperationId", "State", "UndoState")
                SELECT {operation.Id}, i."Id", r."Id", r."Location", i."Description", i."ReceiptItemCode",
                       i."NormalizationRevision", r."NormalizationRevision", c."AssignmentId", a."CategoryId", {selection.TargetCategoryId},
                       gen_random_uuid(), gen_random_uuid(), 'Pending', 'Pending'
                FROM receipts."ReceiptItems" i JOIN receipts."Receipts" r ON r."Id" = i."ReceiptId"
                LEFT JOIN taxonomy."CurrentAssignments" c ON c."ReceiptItemId" = i."Id"
                LEFT JOIN taxonomy."Assignments" a ON a."Id" = c."AssignmentId"
                WHERE i."DeletedAt" IS NULL AND r."DeletedAt" IS NULL
                  AND r."Location" COLLATE "C" = {selection.Group.Store}
                  AND i."Description" COLLATE "C" = {selection.Group.RawDescription}
                  AND i."ReceiptItemCode" COLLATE "C" IS NOT DISTINCT FROM {selection.Group.ItemCode}
                """, ct);
		}
		await transaction.CommitAsync(ct);
		return await ProgressAsync(db, operation, ct);
	}

	public async Task<HistoricalProgress> ReadAsync(string actor, Guid id, CancellationToken ct = default)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		return await ProgressAsync(db, await ManualOwnedAsync(db, actor, id, ct), ct);
	}

	public async Task<HistoricalOperationSummary> ReadSummaryAsync(string actor, Guid id, CancellationToken ct = default)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		return await SummaryAsync(db, await ManualOwnedAsync(db, actor, id, ct), ct);
	}

	private static async Task<HistoricalOperationSummary> SummaryAsync(ApplicationDbContext db,
			HistoricalOperationEntity row, CancellationToken ct)
	{
		var selections = JsonSerializer.Deserialize<HistoricalGroupSelection[]>(row.SelectionJson) ?? [];
		return new(row.Id, row.State, row.CapturedAt, row.TaxonomyRevisionId,
				row.Reason, selections, await ProgressAsync(db, row, ct), row.UndoTaxonomyRevisionId);
	}

	public async Task<HistoricalItemPage> ReadItemsAsync(string actor, Guid id, int offset, int limit, CancellationToken ct = default)
	{
		ValidateLimit(limit);
		if (offset < 0) { throw new ArgumentOutOfRangeException(nameof(offset)); }
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		await ManualOwnedAsync(db, actor, id, ct);
		List<HistoricalOperationItemEntity> rows = await db.HistoricalOperationItems.AsNoTracking().Where(x => x.OperationId == id)
			.OrderBy(x => x.ItemId).Skip(offset).Take(limit + 1).ToListAsync(ct);
		return new(rows.Take(limit).Select(x => new HistoricalItemResult(x.Evidence(), x.State, x.AppliedAssignmentId, x.Reason)).ToArray(),
			rows.Take(limit).Select(x => new HistoricalUndoResult(x.ItemId, x.UndoState, x.UndoAssignmentId, x.UndoReason)).ToArray(),
			rows.Count > limit ? checked(offset + limit) : null);
	}

	public Task<HistoricalProgress> ApplyChunkAsync(string actor, Guid id, int limit, CancellationToken ct = default) =>
		RunChunkAsync(actor, id, null, limit, ct);

	public async Task<HistoricalProgress> UndoChunkAsync(string actor, Guid id, HistoricalUndoCommand command, int limit, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(command);
		await using var db = await factory.CreateDbContextAsync(ct);
		var operation = await OwnedAsync(db, actor, id, false, ct);
		Guid? generation = operation.Kind == HistoricalOperationKind.ApprovedMappingV1
			? await db.CategoryAssistanceSettings.AsNoTracking().Select(x => (Guid?)x.RestoreGeneration).SingleAsync(ct) : null;
		return await RunChunkAsync(actor, id, command, limit, ct, operation.Kind, generation);
	}

	internal Task<HistoricalProgress> UndoApprovedMappingChunkAsync(string actor, Guid id, HistoricalUndoCommand command, int limit, Guid expectedRestoreGeneration, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(command);
		return RunChunkAsync(actor, id, command, limit, ct, HistoricalOperationKind.ApprovedMappingV1, expectedRestoreGeneration);
	}

	public async Task<HistoricalProgress> CancelAsync(string actor, Guid id, CancellationToken ct = default)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		await using var transaction = await db.Database.BeginTransactionAsync(ct);
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		HistoricalOperationEntity operation = await OwnedAsync(db, actor, id, true, ct);
		if (operation.Kind != HistoricalOperationKind.ManualV1)
		{ throw new TaxonomyException(409, "Automatic historical operations cannot be run by the manual service."); }
		operation.State = HistoricalReclassificationPolicy.Cancel(operation.State);
		await db.SaveChangesAsync(ct);
		await transaction.CommitAsync(ct);
		return await ProgressAsync(db, operation, ct);
	}

	private async Task<HistoricalProgress> RunChunkAsync(string actor, Guid id, HistoricalUndoCommand? undo, int limit, CancellationToken ct,
			HistoricalOperationKind kind = HistoricalOperationKind.ManualV1, Guid? expectedRestoreGeneration = null)
	{
		ValidateLimit(limit);
		if (undo != null && (undo.TaxonomyRevisionId == Guid.Empty || string.IsNullOrWhiteSpace(undo.Reason) || undo.Reason.Contains('\0')))
		{ throw new TaxonomyException(400, "Undo taxonomy revision and reason are required."); }
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		await using var transaction = await db.Database.BeginTransactionAsync(ct);
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		if (kind == HistoricalOperationKind.ApprovedMappingV1)
		{
			Guid currentGeneration = await db.CategoryAssistanceSettings.AsNoTracking()
				.Select(x => x.RestoreGeneration).SingleAsync(ct);
			if (expectedRestoreGeneration == Guid.Empty || expectedRestoreGeneration != currentGeneration)
			{ throw new TaxonomyException(409, "Automatic undo request predates restore."); }
		}
		HistoricalOperationEntity operation = await OwnedAsync(db, actor, id, true, ct);
		if (operation.Kind != kind)
		{ throw new TaxonomyException(409, "Historical operation kind does not match the execution path."); }
		if (kind == HistoricalOperationKind.ApprovedMappingV1 && operation.ApprovedMappingRevisionId is null)
		{ throw new TaxonomyException(409, "Automatic historical capture lacks mapping identity."); }
		bool reverting = undo != null;
		if (reverting && operation.UndoTaxonomyRevisionId != null &&
			(operation.UndoTaxonomyRevisionId != undo!.TaxonomyRevisionId || operation.UndoReason != undo.Reason))
		{ throw new TaxonomyException(409, "Undo intent was already frozen with a different request."); }
		if ((!reverting && operation.State is HistoricalOperationState.Completed or HistoricalOperationState.Cancelled or HistoricalOperationState.Undoing or HistoricalOperationState.Undone) ||
			(reverting && operation.State == HistoricalOperationState.Undone))
		{ return await ProgressAsync(db, operation, ct); }
		TaxonomyHeadEntity head = await HeadAsync(db, ct);
		// Only an initial automatic undo can refresh its taxonomy intent. A resumed
		// undo keeps its saved revision so subsequent taxonomy changes still conflict.
		if (reverting && kind == HistoricalOperationKind.ApprovedMappingV1 &&
				operation.UndoTaxonomyRevisionId == null && head.RevisionId != undo!.TaxonomyRevisionId)
		{
			throw new TaxonomyException(409, "Taxonomy changed before automatic undo started. Refresh and retry.",
					new Dictionary<string, object?> { ["code"] = "stale-undo-taxonomy" });
		}
		operation.State = reverting ? HistoricalReclassificationPolicy.BeginUndo(operation.State) : HistoricalReclassificationPolicy.BeginApply(operation.State);
		if (reverting) { operation.UndoTaxonomyRevisionId = undo!.TaxonomyRevisionId; operation.UndoReason = undo.Reason; }
		List<HistoricalOperationItemEntity> rows = await Pending(db, id, reverting).OrderBy(x => x.ItemId).Take(limit).ToListAsync(ct);
		await ReceiptFactLock.AcquireAsync(db, rows.Select(x => x.ReceiptId), ct);
		await NormalizationWriteGuard.LockItemsAsync(db, rows.Select(x => x.ItemId).ToArray(), ct);
		foreach (HistoricalOperationItemEntity row in rows)
		{
			ReceiptItemEntity? item = await db.ReceiptItems.IgnoreAutoIncludes().SingleOrDefaultAsync(x => x.Id == row.ItemId, ct);
			ReceiptEntity? receipt = item == null ? null : await db.Receipts.SingleOrDefaultAsync(x => x.Id == item.ReceiptId, ct);
			Guid? assignment = await db.CurrentClassifications.Where(x => x.ReceiptItemId == row.ItemId).Select(x => (Guid?)x.AssignmentId).SingleOrDefaultAsync(ct);
			Guid? category = reverting ? row.PreviousCategoryId : row.TargetCategoryId;
			bool selectable = category == null || await db.TaxonomyNodes.AnyAsync(x => x.RevisionId == head.RevisionId && x.CategoryId == category && !x.Archived, ct);
			HistoricalCurrentState current = new(item != null && receipt != null,
				new(receipt?.Location ?? "", item?.Description ?? "", item?.ReceiptItemCode),
				item?.NormalizationRevision ?? Guid.Empty, receipt?.NormalizationRevision ?? Guid.Empty,
				assignment, head.RevisionId, selectable);
			HistoricalDecision decision = reverting
				? HistoricalReclassificationPolicy.Undo(row.Evidence(), row.AppliedAssignmentId, undo!.TaxonomyRevisionId, current)
				: HistoricalReclassificationPolicy.Apply(row.Evidence(), operation.TaxonomyRevisionId, current);
			Guid? resultId = null;
			if (decision.Kind == HistoricalDecisionKind.Append)
			{
				ClassificationCommand command = reverting
					? HistoricalReclassificationPolicy.UndoCommand(row.Evidence(), row.AppliedAssignmentId!.Value, undo!.TaxonomyRevisionId, undo.Reason)
					: HistoricalReclassificationPolicy.Command(row.Evidence(), operation.TaxonomyRevisionId, operation.Reason);
				resultId = (await ClassificationService.AssignInTransactionAsync(db, actor, row.ItemId, command, options.Value.WritesEnabled, clock, ct)).Id;
			}
			HistoricalItemState state = decision.Kind switch
			{ HistoricalDecisionKind.Append => HistoricalItemState.Applied, HistoricalDecisionKind.Conflict => HistoricalItemState.Conflicted, _ => HistoricalItemState.Skipped };
			if (reverting) { row.UndoState = state; row.UndoAssignmentId = resultId; row.UndoReason = decision.Reason; }
			else { row.State = state; row.AppliedAssignmentId = resultId; row.Reason = decision.Reason; }
		}
		await db.SaveChangesAsync(ct);
		if (!await Pending(db, id, reverting).AnyAsync(ct))
		{
			operation.State = reverting ? HistoricalOperationState.Undone : HistoricalOperationState.Completed;
			await db.SaveChangesAsync(ct);
		}
		await transaction.CommitAsync(ct);
		return await ProgressAsync(db, operation, ct);
	}

	private static IQueryable<HistoricalOperationItemEntity> Pending(ApplicationDbContext db, Guid id, bool undo) =>
		db.HistoricalOperationItems.Where(x => x.OperationId == id && (undo
			? x.State == HistoricalItemState.Applied && x.UndoState == HistoricalItemState.Pending
			: x.State == HistoricalItemState.Pending));

	private static Task<TaxonomyHeadEntity> HeadAsync(ApplicationDbContext db, CancellationToken ct) =>
		db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE").SingleAsync(ct);

	private static async Task<HistoricalOperationEntity> ManualOwnedAsync(ApplicationDbContext db, string actor, Guid id, CancellationToken ct)
	{
		HistoricalOperationEntity row = await OwnedAsync(db, actor, id, false, ct);
		if (row.Kind != HistoricalOperationKind.ManualV1)
		{ throw new TaxonomyException(404, "Historical operation does not exist."); }
		return row;
	}

	private static async Task<HistoricalOperationEntity> OwnedAsync(ApplicationDbContext db, string actor, Guid id, bool locked, CancellationToken ct)
	{
		IQueryable<HistoricalOperationEntity> query = locked
			? db.HistoricalOperations.FromSqlInterpolated($"SELECT * FROM taxonomy.\"HistoricalOperations\" WHERE \"Id\" = {id} FOR UPDATE")
			: db.HistoricalOperations.AsNoTracking();
		return await query.SingleOrDefaultAsync(x => x.Id == id && x.Actor == actor, ct)
			?? throw new TaxonomyException(404, "Historical operation does not exist.");
	}

	private static void ValidateLimit(int limit)
	{ if (limit is < 1 or > HistoricalReclassificationPolicy.MaximumChunkSize) { throw new ArgumentOutOfRangeException(nameof(limit)); } }

	private static async Task<HistoricalProgress> ProgressAsync(ApplicationDbContext db, HistoricalOperationEntity operation, CancellationToken ct)
	{
		var counts = await db.HistoricalOperationItems.Where(x => x.OperationId == operation.Id).GroupBy(x => 1)
			.Select(g => new
			{
				Total = g.Count(),
				Applied = g.Count(x => x.State == HistoricalItemState.Applied),
				Conflicted = g.Count(x => x.State == HistoricalItemState.Conflicted),
				Skipped = g.Count(x => x.State == HistoricalItemState.Skipped),
				Pending = g.Count(x => x.State == HistoricalItemState.Pending),
				Undone = g.Count(x => x.UndoState == HistoricalItemState.Applied),
				UndoConflicted = g.Count(x => x.UndoState == HistoricalItemState.Conflicted),
				UndoSkipped = g.Count(x => x.UndoState == HistoricalItemState.Skipped)
			}).SingleOrDefaultAsync(ct);
		return new(operation.Id, operation.State, counts?.Total ?? 0, counts?.Applied ?? 0, counts?.Conflicted ?? 0,
			counts?.Skipped ?? 0, counts?.Pending ?? 0, counts?.Undone ?? 0, counts?.UndoConflicted ?? 0, counts?.UndoSkipped ?? 0);
	}
}

