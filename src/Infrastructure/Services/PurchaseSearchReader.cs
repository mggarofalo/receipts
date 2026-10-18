using Application.Interfaces.Services;
using Application.Models.Search;
using Domain.NormalizedDescriptions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class PurchaseSearchReader(IDbContextFactory<ApplicationDbContext> contextFactory) : IPurchaseSearchReader
{
	public async Task<PurchaseSearchResult> SearchAsync(string query, int offset, int limit, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		string pattern = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
		var purchases =
			from item in context.ReceiptItems.IgnoreAutoIncludes().AsNoTracking()
			where item.DeletedAt == null
			join receipt in context.Receipts.IgnoreAutoIncludes().AsNoTracking().Where(r => r.DeletedAt == null)
				on item.ReceiptId equals receipt.Id
			join canonical in context.NormalizedDescriptions.AsNoTracking().Where(n => n.Status == NormalizedDescriptionStatus.Active)
				on item.NormalizedDescriptionId equals canonical.Id into canonicalGroup
			from canonical in canonicalGroup.DefaultIfEmpty()
			where EF.Functions.ILike(item.Description, pattern, @"\")
				|| (item.ReceiptItemCode != null && EF.Functions.ILike(item.ReceiptItemCode, pattern, @"\"))
				|| EF.Functions.ILike(receipt.Location, pattern, @"\")
				|| (canonical != null && EF.Functions.ILike(canonical.CanonicalName, pattern, @"\"))
				|| (canonical != null && canonical.DisplayLabel != null && EF.Functions.ILike(canonical.DisplayLabel, pattern, @"\"))
			select new { Item = item, Receipt = receipt, Canonical = canonical };

		// Search is a live read: concurrent writes may change the set between count and page.
		int total = await purchases.CountAsync(cancellationToken);
		var rows = await purchases.OrderByDescending(x => x.Receipt.Date)
			.ThenBy(x => x.Receipt.Id).ThenBy(x => x.Item.Id)
			.Skip(offset).Take(limit)
			.Select(x => new
			{
				x.Item.Id,
				x.Item.ReceiptId,
				x.Receipt.Date,
				x.Receipt.Location,
				x.Item.Description,
				x.Item.ReceiptItemCode,
				NormalizedDescriptionId = x.Canonical == null ? (Guid?)null : x.Canonical.Id,
				NormalizedDescription = x.Canonical == null ? null
					: string.IsNullOrEmpty(x.Canonical.DisplayLabel) ? x.Canonical.CanonicalName : x.Canonical.DisplayLabel,
				x.Item.Quantity,
				x.Item.UnitPrice,
				x.Item.UnitPriceCurrency,
				x.Item.TotalAmount,
				x.Item.TotalAmountCurrency,
			}).ToListAsync(cancellationToken);

		return new PurchaseSearchResult(rows.Select(x => new PurchaseSearchItem(
			x.Id, x.ReceiptId, x.Date, x.Location, x.Description, x.ReceiptItemCode,
			x.NormalizedDescriptionId, x.NormalizedDescription, x.Quantity,
			x.UnitPrice, x.UnitPriceCurrency.ToString(), x.TotalAmount, x.TotalAmountCurrency.ToString())).ToList(),
			total, offset, limit);
	}
}
