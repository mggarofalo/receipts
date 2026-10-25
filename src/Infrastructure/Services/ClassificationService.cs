using System.Security.Cryptography;
using System.Text.Json;
using Application.Taxonomy;
using Domain.Taxonomy;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public sealed class ClassificationService(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock, Microsoft.Extensions.Options.IOptions<ClassificationOptions> options) : IClassificationService
{
	public async Task<PurchaseClassification> AssignAsync(string actor, Guid itemId, ClassificationCommand command, CancellationToken ct = default)
	{
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		await using var transaction = await db.Database.BeginTransactionAsync(ct);
		await Infrastructure.Utilities.NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		PurchaseClassification result = await AssignInTransactionAsync(db, actor, itemId, command, options.Value.WritesEnabled, clock, ct);
		await transaction.CommitAsync(ct);
		return result;
	}

	internal static async Task<PurchaseClassification> AssignInTransactionAsync(ApplicationDbContext db, string actor, Guid itemId,
		ClassificationCommand command, bool enabled, TimeProvider clock, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(actor) || command.OperationId == Guid.Empty || command.TaxonomyRevisionId == Guid.Empty
			|| string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Contains('\0'))
		{ throw new TaxonomyException(400, "Actor, operation ID, taxonomy revision and reason are required."); }
		if (!enabled) { throw new TaxonomyException(409, "Classification writes require completed migration and portable backup validation."); }
		TaxonomyHeadEntity head = await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE").SingleAsync(ct);
		string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { itemId, command })));
		ClassificationOperationEntity? replay = await db.ClassificationOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Actor == actor && x.OperationId == command.OperationId, ct);
		if (replay != null)
		{
			if (replay.RequestHash != hash) { throw new TaxonomyException(409, "Operation ID was already used with a different request."); }
			return await ReadAsync(db, await db.Classifications.SingleAsync(x => x.Id == replay.AssignmentId, ct), ct);
		}
		if (head.RevisionId != command.TaxonomyRevisionId) { throw new TaxonomyException(409, "Taxonomy changed. Refresh before retrying."); }
		Guid parentId = await db.ReceiptItems.Where(x => x.Id == itemId).Select(x => x.ReceiptId).SingleOrDefaultAsync(ct);
		await ReceiptFactLock.AcquireAsync(db, new List<Guid> { parentId }, ct);
		ReceiptItemEntity? item = await db.ReceiptItems.FromSqlInterpolated($"SELECT * FROM receipts.\"ReceiptItems\" WHERE \"Id\" = {itemId} FOR UPDATE").IgnoreAutoIncludes().SingleOrDefaultAsync(ct);
		if (item == null || !await db.Receipts.AnyAsync(x => x.Id == item.ReceiptId, ct)) { throw new TaxonomyException(404, "Receipt item does not exist."); }
		CurrentClassificationEntity? current = await db.CurrentClassifications.SingleOrDefaultAsync(x => x.ReceiptItemId == itemId, ct);
		if (current?.AssignmentId != command.ExpectedAssignmentId) { throw new TaxonomyException(409, "Classification changed. Refresh before retrying."); }
		if (command.CategoryId is Guid category && !await db.TaxonomyNodes.AnyAsync(x => x.RevisionId == head.RevisionId && x.CategoryId == category && !x.Archived, ct))
		{ throw new TaxonomyException(400, "Category must be selectable in the expected taxonomy revision."); }
		long sequence = current == null ? 1 : 1 + await db.Classifications.Where(x => x.Id == current.AssignmentId).Select(x => x.Sequence).SingleAsync(ct);
		// Persist and return the same PostgreSQL microsecond precision so retries reproduce the original result exactly.
		DateTimeOffset now = clock.GetUtcNow();
		now = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
		PurchaseClassificationEntity assignment = new()
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = itemId,
			Sequence = sequence,
			PreviousAssignmentId = current?.AssignmentId,
			CategoryId = command.CategoryId,
			TaxonomyRevisionId = head.RevisionId,
			RecordedAt = now,
			Actor = actor,
			Source = "manual",
			Reason = command.Reason,
			OperationId = command.OperationId
		};
		db.Classifications.Add(assignment);
		if (current == null) { db.CurrentClassifications.Add(new() { ReceiptItemId = itemId, AssignmentId = assignment.Id }); }
		else { current.AssignmentId = assignment.Id; }
		db.ClassificationOperations.Add(new() { Actor = actor, OperationId = command.OperationId, RequestHash = hash, AssignmentId = assignment.Id });
		await db.SaveChangesAsync(ct);
		PurchaseClassification result = await ReadAsync(db, assignment, ct);
		return result;
	}

	internal static void RequireRequestId(IEnumerable<ReceiptItemEntity> items, Guid? requestId)
	{
		if (items.Any(x => x.Classification != null) && (requestId == null || requestId == Guid.Empty))
		{ throw new TaxonomyException(400, "Explicit aggregate classification requires a nonempty clientRequestId for atomic retries."); }
	}

	internal static async Task ProtectLegacyEditAsync(ApplicationDbContext db, ReceiptItemEntity incoming, ReceiptItemEntity stored, CancellationToken ct)
	{
		if (incoming.Classification == null && (incoming.Category != stored.Category || incoming.Subcategory != stored.Subcategory)
			&& await db.CurrentClassifications.AnyAsync(x => x.ReceiptItemId == stored.Id, ct))
		{ throw new TaxonomyException(409, "This item has a versioned classification. Use an explicit classification choice to change its category."); }
	}

	internal static async Task PrepareAggregateAsync(ApplicationDbContext db, IEnumerable<ReceiptItemEntity> items, bool enabled, CancellationToken ct)
	{
		if (!items.Any(x => x.Classification != null)) { return; }
		if (!enabled) { throw new TaxonomyException(409, "Classification writes require completed migration and portable backup validation."); }
		await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE").SingleAsync(ct);
	}

	internal static async Task ApplyAggregateAsync(ApplicationDbContext db, IEnumerable<ReceiptItemEntity> items, bool enabled, CancellationToken ct)
	{
		foreach (ReceiptItemEntity item in items.OrderBy(x => x.Id))
		{
			if (item.Classification is not { } intent) { continue; }
			string actor = db.ReceiptWriteActor ?? throw new TaxonomyException(400, "A signed-in actor is required for classification.");
			PurchaseClassification result = await AssignInTransactionAsync(db, actor, item.Id,
				new(intent.CategoryId, intent.TaxonomyRevisionId, intent.ExpectedAssignmentId, intent.OperationId, intent.Reason), enabled, TimeProvider.System, ct);
			string category = result.Path.FirstOrDefault()?.Name ?? item.Category;
			string? subcategory = result.Path.Skip(1).FirstOrDefault()?.Name;
			if (intent.CategoryId != null && (item.Category != category || item.Subcategory != subcategory))
			{ throw new TaxonomyException(409, "Legacy category labels contradict the explicit classification path."); }
		}
	}

	public async Task<ClassificationHistory> HistoryAsync(Guid itemId, long? beforeSequence, int limit, CancellationToken ct = default)
	{
		if (limit is < 1 or > 100 || beforeSequence is <= 0) { throw new TaxonomyException(400, "Limit must be 1–100 and cursor must be positive."); }
		await using ApplicationDbContext db = await factory.CreateDbContextAsync(ct);
		if (!await db.ReceiptItems.AnyAsync(x => x.Id == itemId && db.Receipts.Any(r => r.Id == x.ReceiptId), ct)) { throw new TaxonomyException(404, "Receipt item does not exist."); }
		List<PurchaseClassificationEntity> rows = await db.Classifications.AsNoTracking().Where(x => x.ReceiptItemId == itemId && (!beforeSequence.HasValue || x.Sequence < beforeSequence))
			.OrderByDescending(x => x.Sequence).Take(limit + 1).ToListAsync(ct);
		List<PurchaseClassification> items = [];
		foreach (PurchaseClassificationEntity row in rows.Take(limit)) { items.Add(await ReadAsync(db, row, ct)); }
		return new(items, rows.Count > limit ? items[^1].Sequence : null);
	}

	private static async Task<PurchaseClassification> ReadAsync(ApplicationDbContext db, PurchaseClassificationEntity row, CancellationToken ct)
	{
		List<TaxonomyNode> path = [];
		if (row.CategoryId is Guid category)
		{
			Dictionary<Guid, TaxonomyNodeEntity> nodes = await db.TaxonomyNodes.AsNoTracking().Where(x => x.RevisionId == row.TaxonomyRevisionId).ToDictionaryAsync(x => x.CategoryId, ct);
			Guid? cursor = category;
			while (cursor.HasValue) { TaxonomyNodeEntity node = nodes[cursor.Value]; path.Add(new(node.CategoryId, node.ParentId, node.Name, node.Definition, node.Archived)); cursor = node.ParentId; }
			path.Reverse();
		}
		return new(row.Id, row.ReceiptItemId, row.Sequence, row.PreviousAssignmentId, row.CategoryId, row.TaxonomyRevisionId,
			row.RecordedAt, row.Actor, row.Source, row.Reason, row.OperationId, path);
	}
}
