using System.Security.Cryptography;
using System.Text.Json;
using Application.Normalization;
using Application.Taxonomy;
using Infrastructure.Entities.CategoryAssistance;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

// Internal execution seam only. Activation and portable export are not enabled.
internal sealed class HistoricalApprovedMappingExecutor(
	IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock, IOptions<ClassificationOptions> options)
{
	// The manual correction is durable intent: subsequent automatic mapping execution
	// treats both a manual category and an explicit manual null as protected.
	// The initiating user request captures this generation once and passes it to every chunk.
	// Never refresh it in a worker: restore rotates it while holding the shared write gate.
	internal Task<HistoricalProgress> UndoChunkAsync(Guid expectedRestoreGeneration, string actor, Guid id, HistoricalUndoCommand command, int limit, CancellationToken ct = default) =>
			new HistoricalReclassificationService(factory, clock, options).UndoApprovedMappingChunkAsync(actor, id, command, limit, expectedRestoreGeneration, ct);

	internal async Task<HistoricalProgress> ApplyChunkAsync(Guid expectedRestoreGeneration, string actor, Guid id, int limit, CancellationToken ct = default)
	{
		if (limit is < 1 or > HistoricalReclassificationPolicy.MaximumChunkSize)
		{ throw new ArgumentOutOfRangeException(nameof(limit)); }
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var tx = await db.Database.BeginTransactionAsync(ct);
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		await HistoricalRestoreGeneration.RequireCurrentAsync(db, expectedRestoreGeneration, ct);
		var operation = await db.HistoricalOperations.FromSqlInterpolated(
			$"SELECT * FROM taxonomy.\"HistoricalOperations\" WHERE \"Id\" = {id} FOR UPDATE")
			.SingleOrDefaultAsync(x => x.Actor == actor, ct)
			?? throw new TaxonomyException(404, "Historical operation does not exist.");
		if (operation.Kind != HistoricalOperationKind.ApprovedMappingV1 || operation.ApprovedMappingRevisionId is null)
		{ throw new TaxonomyException(409, "Operation is not an approved mapping capture."); }
		if (operation.State is HistoricalOperationState.Completed or HistoricalOperationState.Cancelled or HistoricalOperationState.Undoing or HistoricalOperationState.Undone)
		{ return await ProgressAsync(db, operation, ct); }
		operation.State = HistoricalReclassificationPolicy.BeginApply(operation.State);
		var rows = await db.HistoricalOperationItems.Where(x => x.OperationId == id && x.State == HistoricalItemState.Pending)
			.OrderBy(x => x.ItemId).Take(limit).ToListAsync(ct);
		// Capture contains one exact source. Lock every distinct source deterministically in case
		// corrupted data violates that invariant. Disposition writers use this same advisory lock.
		var groups = rows.Select(x => new HistoricalGroupKey(x.Store, x.RawDescription, x.ItemCode))
			.Distinct().Select(x => (Group: x, Hash: HistoricalReclassificationService.DispositionSourceHash(x)))
			.OrderBy(x => x.Hash, StringComparer.Ordinal).ToArray();
		foreach (var group in groups)
		{ await HistoricalReclassificationService.LockDispositionSourceAsync(db, actor, group.Hash, ct); }
		var head = await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE").SingleAsync(ct);
		await ReceiptFactLock.AcquireAsync(db, rows.Select(x => x.ReceiptId), ct);
		await NormalizationWriteGuard.LockItemsAsync(db, rows.Select(x => x.ItemId).ToArray(), ct);
		var scope = ReadScope(operation.SelectionJson);
		foreach (var row in rows)
		{
			if (!MatchesCapture(operation, row, scope))
			{
				row.State = HistoricalItemState.Skipped;
				row.Reason = "invalid-capture-evidence";
				continue;
			}
			var group = new HistoricalGroupKey(row.Store, row.RawDescription, row.ItemCode);
			string hash = HistoricalReclassificationService.DispositionSourceHash(group);
			var disposition = await db.HistoricalGroupDispositions.AsNoTracking()
				.SingleOrDefaultAsync(x => x.Actor == actor && x.SourceHash == hash, ct);
			// A collision must never turn a disposition into permission to write.
			bool deferred = disposition != null && (disposition.Store != row.Store || disposition.RawDescription != row.RawDescription
				|| disposition.ItemCode != row.ItemCode || disposition.Disposition != null);
			var item = await db.ReceiptItems.IgnoreQueryFilters().IgnoreAutoIncludes().AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.ItemId, ct);
			var receipt = item == null ? null : await db.Receipts.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.ReceiptId, ct);
			var assignment = await (from current in db.CurrentClassifications.AsNoTracking()
									join a in db.Classifications.AsNoTracking() on current.AssignmentId equals a.Id
									where current.ReceiptItemId == row.ItemId
									select a).SingleOrDefaultAsync(ct);
			var source = NormalizationCatalogPolicy.Normalize(new(row.Store, row.RawDescription, row.ItemCode));
			string fingerprint = NormalizationCatalogPolicy.Hash(source);
			var resolution = await CategoryMappingService.ResolveInTransactionAsync(db, source, fingerprint, head.RevisionId, null, ct);
			var preferences = await (from preference in db.CategoryAssistanceReviewPreferences.AsNoTracking()
									 join attempt in db.CategoryAssistanceAttempts.AsNoTracking() on preference.AttemptId equals attempt.Id
									 where attempt.Location == source.Location && attempt.Description == source.Description && attempt.ItemCodeKey == (source.ItemCode ?? "")
									 select preference).ToListAsync(ct);
			bool protectedByPreference = preferences.Any(p => ProtectsItem(p, row.ItemId));
			bool eligibleAssignment = assignment == null || (assignment.CategoryId == null &&
				(assignment.Source == "legacy_import" || (assignment.Source == "legacy_write" && assignment.LegacyResolution == "unresolved_path")));
			bool eligible = !deferred && !protectedByPreference && item != null && receipt != null &&
				item.DeletedAt == null && receipt.DeletedAt == null && item.ReceiptId == row.ReceiptId &&
				item.NormalizationRevision == row.ItemRevision && receipt.NormalizationRevision == row.ReceiptRevision &&
				receipt.Location == row.Store && item.Description == row.RawDescription && item.ReceiptItemCode == row.ItemCode &&
				string.IsNullOrWhiteSpace(item.Category) && string.IsNullOrWhiteSpace(item.Subcategory) &&
				assignment?.Id == row.ExpectedAssignmentId && eligibleAssignment && head.RevisionId == operation.TaxonomyRevisionId &&
				resolution.Disposition == "approved" && resolution.MappingRevisionId == row.ApprovedMappingRevisionId &&
				resolution.CategoryId == row.TargetCategoryId && resolution.CategoryId.HasValue && resolution.ApprovalTaxonomyRevisionId.HasValue;
			if (!eligible)
			{
				row.State = HistoricalItemState.Skipped;
				row.Reason = "stale-or-protected";
				continue;
			}
			var result = await ClassificationService.AssignApprovedMappingInTransactionAsync(db, actor, row.ItemId,
				resolution.CategoryId!.Value, head.RevisionId, row.ExpectedAssignmentId, row.ApplyOperationId,
				new(resolution.MappingRevisionId!.Value, resolution.ApprovalTaxonomyRevisionId!.Value, fingerprint),
				options.Value.WritesEnabled, clock, ct);
			row.State = HistoricalItemState.Applied;
			row.AppliedAssignmentId = result.Id;
		}
		await db.SaveChangesAsync(ct);
		if (!await db.HistoricalOperationItems.AnyAsync(x => x.OperationId == id && x.State == HistoricalItemState.Pending, ct))
		{ operation.State = HistoricalOperationState.Completed; await db.SaveChangesAsync(ct); }
		await tx.CommitAsync(ct);
		return await ProgressAsync(db, operation, ct);
	}

	private sealed record CaptureScope(HistoricalGroupKey? Group, Guid? TargetCategoryId, Guid[]? ExcludedItemIds);

	private static CaptureScope? ReadScope(string json)
	{
		try
		{
			var scopes = JsonSerializer.Deserialize<CaptureScope[]>(json);
			if (scopes is not { Length: 1 } || scopes[0] is not { Group: not null, TargetCategoryId: not null, ExcludedItemIds: not null })
			{ return null; }
			var scope = scopes[0];
			if (scope.TargetCategoryId == Guid.Empty || scope.ExcludedItemIds.Length > 1000 ||
				scope.ExcludedItemIds.Any(x => x == Guid.Empty) ||
				scope.ExcludedItemIds.Distinct().Count() != scope.ExcludedItemIds.Length ||
				!scope.ExcludedItemIds.SequenceEqual(scope.ExcludedItemIds.Order()))
			{ return null; }
			return scope;
		}
		catch (JsonException) { return null; }
	}

	private static bool MatchesCapture(HistoricalOperationEntity operation, HistoricalOperationItemEntity row, CaptureScope? scope)
	{
		if (scope?.Group == null || scope.ExcludedItemIds == null || row.ApprovedMappingRevisionId is not Guid mapping ||
			mapping == Guid.Empty || mapping != operation.ApprovedMappingRevisionId || row.TargetCategoryId != scope.TargetCategoryId || scope.ExcludedItemIds.Contains(row.ItemId) ||
			scope.Group != new HistoricalGroupKey(row.Store, row.RawDescription, row.ItemCode))
		{ return false; }
		// Recreate the exact capture request digest, binding mapping, taxonomy, actor, source and exclusions.
		string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
		{
			operation.Actor,
			operation.TaxonomyRevisionId,
			MappingRevisionId = mapping,
			Group = scope.Group,
			ExcludedItemIds = scope.ExcludedItemIds
		})));
		return hash == operation.RequestHash;
	}

	private static bool ProtectsItem(CategoryAssistanceReviewPreferenceEntity preference, Guid itemId)
	{
		if (string.IsNullOrWhiteSpace(preference.Actor) || preference.AttemptId == Guid.Empty || preference.Revision == Guid.Empty ||
			preference.State is not ("active" or "later" or "left_as_is")) { return true; }
		try
		{
			var ids = JsonSerializer.Deserialize<Guid[]>(preference.ExcludedItemIdsJson);
			if (ids == null || ids.Length > 1000 || ids.Any(x => x == Guid.Empty) || ids.Distinct().Count() != ids.Length)
			{ return true; }
			return preference.State != "active" || ids.Contains(itemId);
		}
		catch (JsonException) { return true; }
		catch (ArgumentNullException) { return true; }
	}

	private static async Task<HistoricalProgress> ProgressAsync(ApplicationDbContext db, HistoricalOperationEntity operation, CancellationToken ct)
	{
		var rows = await db.HistoricalOperationItems.AsNoTracking().Where(x => x.OperationId == operation.Id).ToListAsync(ct);
		return new(operation.Id, operation.State, rows.Count, rows.Count(x => x.State == HistoricalItemState.Applied),
			rows.Count(x => x.State == HistoricalItemState.Conflicted), rows.Count(x => x.State == HistoricalItemState.Skipped),
						rows.Count(x => x.State == HistoricalItemState.Pending), rows.Count(x => x.UndoState == HistoricalItemState.Applied),
						rows.Count(x => x.UndoState == HistoricalItemState.Conflicted), rows.Count(x => x.UndoState == HistoricalItemState.Skipped));
	}
}
