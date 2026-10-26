using Infrastructure.Entities.Taxonomy;
using Infrastructure.Interfaces;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	internal static async Task LockDestinationAsync(ApplicationDbContext db, CancellationToken ct)
	{
		await NormalizationWriteGuard.AcquireWriteGateAsync(db, ct);
		if (!db.Database.IsNpgsql()) { return; }
		// Fence even older business writers which do not participate in the application gate.
		// EXCLUSIVE also waits for existing SELECT FOR UPDATE workers before taking
		// the table fence; otherwise their later vector UPDATE can deadlock with restore.
		// Match embedding source order: canonical descriptions, templates, then receipt items.
		// The head remains locked through projection rebuild and transaction commit.
		await db.Database.ExecuteSqlRawAsync("""
            LOCK TABLE matching."NormalizedDescriptions", library."Categories", library."Subcategories", library."ItemTemplates",
            receipts."Receipts", receipts."ReceiptItems", receipts."Transactions", receipts."Adjustments",
            receipts."AcceptedDuplicatePairs", ynab."YnabSyncRecords"
            IN EXCLUSIVE MODE
            """, ct);
		await db.TaxonomyHeads.FromSqlRaw("SELECT * FROM taxonomy.\"Head\" WHERE \"Id\" = 1 FOR UPDATE").SingleAsync(ct);
	}

	internal static async Task RestoreAsync(ApplicationDbContext db, State incoming, List<DeletionState> deletion, CancellationToken ct)
	{
		async Task Append<T>(IEnumerable<T> rows) where T : class
		{
			var keys = (await db.Set<T>().AsNoTracking().ToListAsync(ct)).Select(ArchiveKey).ToHashSet(StringComparer.Ordinal);
			db.Set<T>().AddRange(rows.Where(row => !keys.Contains(ArchiveKey(row))));
			await db.SaveChangesAsync(ct);
		}
		await Append(incoming.Identities);
		var storedRevisions = (await db.TaxonomyRevisions.AsNoTracking().Select(r => r.Id).ToListAsync(ct)).ToHashSet();
		foreach (var revision in incoming.Revisions.OrderBy(r => r.Sequence).Where(r => !storedRevisions.Contains(r.Id)))
		{
			// Revision and every node are inserted within this transaction. PostgreSQL stamps
			// its own transaction ID; sealed snapshots are never updated or supplemented.
			db.TaxonomyRevisions.Add(revision);
			await db.SaveChangesAsync(ct);
			var pending = incoming.Nodes.Where(n => n.RevisionId == revision.Id).ToList();
			HashSet<Guid> inserted = [];
			while (pending.Count > 0)
			{
				var level = pending.Where(n => n.ParentId == null || inserted.Contains(n.ParentId.Value)).ToList();
				Require(level.Count > 0, "Invalid snapshot parent order.");
				db.TaxonomyNodes.AddRange(level);
				await db.SaveChangesAsync(ct);
				foreach (var node in level) { inserted.Add(node.CategoryId); pending.Remove(node); }
			}
		}
		await RestoreMappingsAsync(db, incoming, ct);
		await Append(incoming.Operations);
		// Explicit sequence ordering also satisfies immediate previous-assignment FKs.
		foreach (var group in incoming.Assignments.GroupBy(a => a.Sequence).OrderBy(g => g.Key))
		{ await Append(group); }
		await Append(incoming.ClassificationOperations);
		await Append(incoming.Migrations);
		await Append(incoming.Crosswalks);
		await Append(incoming.MigrationItems);

		// History is authoritative: rebuild projections from its latest per-item assignment.
		var current = await db.CurrentClassifications.ToDictionaryAsync(c => c.ReceiptItemId, ct);
		foreach (var group in incoming.Assignments.GroupBy(a => a.ReceiptItemId))
		{
			Guid latest = group.MaxBy(a => a.Sequence)!.Id;
			if (current.TryGetValue(group.Key, out var row)) { row.AssignmentId = latest; }
			else { db.CurrentClassifications.Add(new() { ReceiptItemId = group.Key, AssignmentId = latest }); }
		}
		foreach (var row in deletion.Where(r => r.Table != "accepted_duplicate_pairs"))
		{
			object? entity = row.Table switch
			{
				"categories" => await db.Categories.FindAsync([row.Id], ct),
				"subcategories" => await db.Subcategories.FindAsync([row.Id], ct),
				"item_templates" => await db.ItemTemplates.FindAsync([row.Id], ct),
				"receipts" => await db.Receipts.FindAsync([row.Id], ct),
				"receipt_items" => await db.ReceiptItems.FindAsync([row.Id], ct),
				"transactions" => await db.Transactions.FindAsync([row.Id], ct),
				"adjustments" => await db.Adjustments.FindAsync([row.Id], ct),
				"ynab_sync_records" => await db.YnabSyncRecords.FindAsync([row.Id], ct),
				"accepted_duplicate_pairs" => await db.AcceptedDuplicatePairs.FindAsync([row.Id], ct),
				_ => null
			};
			Require(entity is ISoftDeletable, "Restored deletion target is missing.");
			var target = (ISoftDeletable)entity!;
			target.DeletedAt = row.DeletedAt;
			target.DeletedByUserId = row.DeletedByUserId;
			target.DeletedByApiKeyId = row.DeletedByApiKeyId;
			target.CascadeDeletedByParentId = row.CascadeDeletedByParentId;
		}
		await db.SaveChangesAsync(ct);
		// Saving receipt/payment deletion can invalidate duplicate decisions via direct SQL.
		// Restore their archived state after those hooks finish, refreshing tracked values.
		foreach (var row in deletion.Where(r => r.Table == "accepted_duplicate_pairs"))
		{
			var pair = await db.AcceptedDuplicatePairs.IgnoreQueryFilters().SingleAsync(p => p.Id == row.Id, ct);
			await db.Entry(pair).ReloadAsync(ct);
			pair.DeletedAt = row.DeletedAt;
			pair.DeletedByUserId = row.DeletedByUserId;
			pair.DeletedByApiKeyId = row.DeletedByApiKeyId;
			pair.CascadeDeletedByParentId = row.CascadeDeletedByParentId;
		}
		await db.SaveChangesAsync(ct);
		(await db.TaxonomyHeads.SingleAsync(ct)).RevisionId = incoming.Heads.Single().RevisionId;
		await db.SaveChangesAsync(ct);
	}
}
