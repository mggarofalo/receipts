using System.Security.Cryptography;
using System.Text.Json;
using Application.Normalization;
using Application.Taxonomy;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

internal sealed record ApprovedMappingCaptureRequest(Guid OperationId, string Actor, Guid TaxonomyRevisionId,
	Guid MappingRevisionId, HistoricalGroupKey Group, IReadOnlyList<Guid> ExcludedItemIds, Guid ExpectedRestoreGeneration);

// Capture only. No worker or public entry point invokes this service.
internal sealed class HistoricalApprovedMappingCapture(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock)
{
	internal async Task<int> CaptureAsync(ApprovedMappingCaptureRequest request, CancellationToken ct = default)
	{
		await using var db = await factory.CreateDbContextAsync(ct);
		await using var transaction = await db.Database.BeginTransactionAsync(ct);
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		await HistoricalRestoreGeneration.RequireCurrentAsync(db, request.ExpectedRestoreGeneration, ct);
		int count = await CapturePageInTransactionAsync(db, request, null, false, ct);
		await transaction.CommitAsync(ct);
		return count;
	}

	// Caller owns the write gate, restore fence, and transaction.
	internal async Task<int> CapturePageInTransactionAsync(ApplicationDbContext db, ApprovedMappingCaptureRequest request,
		Guid? after, bool paged, CancellationToken ct)
	{
		if (request.OperationId == Guid.Empty || request.TaxonomyRevisionId == Guid.Empty ||
			request.MappingRevisionId == Guid.Empty || string.IsNullOrWhiteSpace(request.Actor) || request.Actor.Contains('\0') ||
			request.Group == null || string.IsNullOrWhiteSpace(request.Group.Store) || string.IsNullOrWhiteSpace(request.Group.RawDescription) ||
			request.Group.Store.Contains('\0') || request.Group.RawDescription.Contains('\0') || request.Group.ItemCode?.Contains('\0') == true ||
			request.ExcludedItemIds == null || request.ExcludedItemIds.Count > 1000 || request.ExcludedItemIds.Any(x => x == Guid.Empty) ||
			request.ExcludedItemIds.Distinct().Count() != request.ExcludedItemIds.Count)
		{ throw new TaxonomyException(400, "Valid operation, actor, source, revisions and exclusions are required."); }
		Guid[] excluded = request.ExcludedItemIds.Order().ToArray();
		var source = NormalizationCatalogPolicy.Normalize(new(request.Group.Store, request.Group.RawDescription, request.Group.ItemCode));
		// The database provenance guard requires exact source values, not normalized equivalence.
		if (source.Location != request.Group.Store || source.Description != request.Group.RawDescription || source.ItemCode != request.Group.ItemCode)
		{ throw new TaxonomyException(400, "Source must exactly match the approved mapping key."); }
		string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
		{ request.Actor, request.TaxonomyRevisionId, request.MappingRevisionId, request.Group, ExcludedItemIds = excluded })));
		var existing = await db.HistoricalOperations.SingleOrDefaultAsync(x => x.Id == request.OperationId, ct);
		if (existing != null)
		{
			if (existing.Kind != HistoricalOperationKind.ApprovedMappingV1 || existing.ApprovedMappingRevisionId != request.MappingRevisionId || existing.Actor != request.Actor || existing.RequestHash != hash)
			{ throw new TaxonomyException(409, "Operation ID was already used with a different request."); }
			return await db.HistoricalOperationItems.CountAsync(x => x.OperationId == existing.Id, ct);
		}
		// Dispositions are actor-private. Serialize against their writer, then check under the same lock.
		// A future executor must recheck this decision; capture alone is not write authority.
		string dispositionHash = HistoricalReclassificationService.DispositionSourceHash(request.Group);
		await HistoricalReclassificationService.LockDispositionSourceAsync(db, request.Actor, dispositionHash, ct);
		var disposition = await db.HistoricalGroupDispositions.AsNoTracking()
			.SingleOrDefaultAsync(x => x.Actor == request.Actor && x.SourceHash == dispositionHash, ct);
		if (disposition != null && (disposition.Store != request.Group.Store ||
			disposition.RawDescription != request.Group.RawDescription || disposition.ItemCode != request.Group.ItemCode))
		{ throw new TaxonomyException(409, "Source hash collision."); }
		if (disposition?.Disposition != null)
		{ throw new TaxonomyException(409, "This source is deferred or marked leave as is."); }
		var head = await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE").SingleAsync(ct);
		if (head.RevisionId != request.TaxonomyRevisionId) { throw new TaxonomyException(409, "Taxonomy changed."); }
		var resolution = await CategoryMappingService.ResolveInTransactionAsync(db, source,
			NormalizationCatalogPolicy.Hash(source), head.RevisionId, null, ct);
		if (resolution.Disposition != "approved" || resolution.MappingRevisionId != request.MappingRevisionId || resolution.CategoryId == null)
		{ throw new TaxonomyException(409, "Approved mapping changed or is no longer valid."); }
		var operation = new HistoricalOperationEntity
		{
			Id = request.OperationId,
			Kind = HistoricalOperationKind.ApprovedMappingV1,
			ApprovedMappingRevisionId = request.MappingRevisionId,
			Actor = request.Actor,
			TaxonomyRevisionId = head.RevisionId,
			Reason = "Approved exact-source mapping capture.",
			RequestHash = hash,
			SelectionJson = JsonSerializer.Serialize(new[] { new { request.Group, TargetCategoryId = resolution.CategoryId, ExcludedItemIds = excluded } }),
			CapturedAt = clock.GetUtcNow(),
			State = HistoricalOperationState.Preview
		};
		db.HistoricalOperations.Add(operation);
		await db.SaveChangesAsync(ct);
		int inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO taxonomy."HistoricalOperationItems"
            ("OperationId", "ItemId", "ReceiptId", "ApprovedMappingRevisionId", "Store", "RawDescription", "ItemCode",
             "ItemRevision", "ReceiptRevision", "ExpectedAssignmentId", "PreviousCategoryId", "TargetCategoryId",
             "ApplyOperationId", "UndoOperationId", "State", "UndoState")
            SELECT {operation.Id}, i."Id", r."Id", {request.MappingRevisionId}, r."Location", i."Description", i."ReceiptItemCode",
                   i."NormalizationRevision", r."NormalizationRevision", c."AssignmentId", a."CategoryId", {resolution.CategoryId},
                   gen_random_uuid(), gen_random_uuid(), 'Pending', 'Pending'
            FROM receipts."ReceiptItems" i JOIN receipts."Receipts" r ON r."Id" = i."ReceiptId"
            LEFT JOIN taxonomy."CurrentAssignments" c ON c."ReceiptItemId" = i."Id"
            LEFT JOIN taxonomy."Assignments" a ON a."Id" = c."AssignmentId"
            WHERE i."DeletedAt" IS NULL AND r."DeletedAt" IS NULL
              AND r."Location" COLLATE "C" = {request.Group.Store}
              AND i."Description" COLLATE "C" = {request.Group.RawDescription}
              AND i."ReceiptItemCode" COLLATE "C" IS NOT DISTINCT FROM {request.Group.ItemCode}
              AND NOT (i."Id" = ANY ({excluded}))
              AND (CAST({after} AS uuid) IS NULL OR i."Id" > CAST({after} AS uuid))
              AND nullif(btrim(i."Category"), '') IS NULL AND nullif(btrim(i."Subcategory"), '') IS NULL
              AND (c."AssignmentId" IS NULL OR (a."CategoryId" IS NULL AND
                   (a."Source" = 'legacy_import' OR (a."Source" = 'legacy_write' AND a."LegacyResolution" = 'unresolved_path'))))
            ORDER BY i."Id"
            LIMIT {(paged ? 100 : 101)}
            """, ct);
		if (inserted > 100) { throw new TaxonomyException(400, "An exact source cannot capture more than 100 eligible purchases."); }
		return inserted;
	}
}
