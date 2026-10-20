using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Utilities;

/// <summary>Approved historical snapshots retain ownership until processed or their observed source changes.</summary>
internal static class NormalizationCatalogReservations
{
	internal static IQueryable<Guid> ItemIds(ApplicationDbContext context)
		=> from member in context.NormalizationCatalogMembers
		   join operation in context.NormalizationCatalogOperations on member.OperationId equals operation.Id
		   join item in context.ReceiptItems.IgnoreAutoIncludes().IgnoreQueryFilters() on member.ItemId equals item.Id
		   join receipt in context.Receipts.IgnoreQueryFilters() on member.ReceiptId equals receipt.Id
		   where member.State == "pending" && operation.MappingId != null
			   && item.NormalizationRevision == member.SourceRevision && receipt.NormalizationRevision == member.ReceiptRevision
			   && item.DeletedAt == null && receipt.DeletedAt == null
		   select item.Id;
}
