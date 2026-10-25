using System.Runtime.CompilerServices;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public partial class BackupImportService
{
	private static readonly ConditionalWeakTable<ApplicationDbContext, Dictionary<(string, Guid), PortableTaxonomyArchive.DeletionState>> ImportDeletionStates = new();

	private static string? DeletionTable(object entity) => entity switch
	{
		CategoryEntity => "categories",
		SubcategoryEntity => "subcategories",
		ItemTemplateEntity => "item_templates",
		ReceiptEntity => "receipts",
		ReceiptItemEntity => "receipt_items",
		TransactionEntity => "transactions",
		AdjustmentEntity => "adjustments",
		YnabSyncRecordEntity => "ynab_sync_records",
		AcceptedDuplicatePairEntity => "accepted_duplicate_pairs",
		_ => null
	};
	private static void ApplyDeletion(ISoftDeletable target, PortableTaxonomyArchive.DeletionState row)
	{
		target.DeletedAt = row.DeletedAt;
		target.DeletedByUserId = row.DeletedByUserId;
		target.DeletedByApiKeyId = row.DeletedByApiKeyId;
		target.CascadeDeletedByParentId = row.CascadeDeletedByParentId;
	}
	private static Task<int> SaveImportedChangesAsync(ApplicationDbContext context, CancellationToken ct)
	{
		// Apply before the context's admission, audit and uniqueness checks, including newly added rows.
		if (ImportDeletionStates.TryGetValue(context, out var states))
		{
			foreach (var entry in context.ChangeTracker.Entries().ToList())
			{
				string? table = DeletionTable(entry.Entity);
				if (table != null && entry.Entity is ISoftDeletable target
					&& states.TryGetValue((table, (Guid)entry.Property("Id").CurrentValue!), out var row))
				{ ApplyDeletion(target, row); }
			}
		}
		return context.SaveChangesAsync(ct);
	}
	private static async Task StageImportedTombstonesAsync(ApplicationDbContext db,
		List<PortableTaxonomyArchive.DeletionState> states, CancellationToken ct)
	{
		// Free filtered unique slots in a separate save before any incoming active row can claim them.
		// Loading by key ignores filters and never changes immutable taxonomy history.
		foreach (var row in states.Where(r => r.DeletedAt != null))
		{
			Type kind = row.Table switch
			{
				"categories" => typeof(CategoryEntity),
				"subcategories" => typeof(SubcategoryEntity),
				"item_templates" => typeof(ItemTemplateEntity),
				"receipts" => typeof(ReceiptEntity),
				"receipt_items" => typeof(ReceiptItemEntity),
				"transactions" => typeof(TransactionEntity),
				"adjustments" => typeof(AdjustmentEntity),
				"ynab_sync_records" => typeof(YnabSyncRecordEntity),
				"accepted_duplicate_pairs" => typeof(AcceptedDuplicatePairEntity),
				_ => throw new InvalidDataException("Unknown deletion kind.")
			};
			if (await db.FindAsync(kind, [row.Id], ct) is ISoftDeletable target) { ApplyDeletion(target, row); }
		}
		await SaveImportedChangesAsync(db, ct);
	}
}
