using Application.Interfaces.Services;
using Application.Models.Search;
using Domain.NormalizedDescriptions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class PurchaseSearchReader(IDbContextFactory<ApplicationDbContext> contextFactory) : IPurchaseSearchReader
{
	public async Task<PurchaseSearchResult> SearchAsync(string query, int offset, int limit,
		PurchaseSearchOptions options, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		string pattern = LiteralPattern(query);
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

		if (!string.IsNullOrEmpty(options.Location))
		{
			string locationPattern = LiteralPattern(options.Location);
			purchases = purchases.Where(x => EF.Functions.ILike(x.Receipt.Location, locationPattern, @"\"));
		}
		if (options.StartDate.HasValue)
		{
			purchases = purchases.Where(x => x.Receipt.Date >= options.StartDate.Value);
		}
		if (options.EndDate.HasValue)
		{
			purchases = purchases.Where(x => x.Receipt.Date <= options.EndDate.Value);
		}
		if (options.MinUnitPrice.HasValue)
		{
			purchases = purchases.Where(x => x.Item.UnitPrice >= options.MinUnitPrice.Value);
		}
		if (options.MaxUnitPrice.HasValue)
		{
			purchases = purchases.Where(x => x.Item.UnitPrice <= options.MaxUnitPrice.Value);
		}

		// Search is a live read: concurrent writes may change the set between count and page.
		int total = await purchases.CountAsync(cancellationToken);
		bool descending = options.SortDirection == "desc";
		var ordered = options.SortBy switch
		{
			"date" => descending ? purchases.OrderByDescending(x => x.Receipt.Date) : purchases.OrderBy(x => x.Receipt.Date),
			"description" => descending ? purchases.OrderByDescending(x => x.Item.Description) : purchases.OrderBy(x => x.Item.Description),
			"location" => descending ? purchases.OrderByDescending(x => x.Receipt.Location) : purchases.OrderBy(x => x.Receipt.Location),
			"unitPrice" => descending ? purchases.OrderByDescending(x => x.Item.UnitPrice) : purchases.OrderBy(x => x.Item.UnitPrice),
			"quantity" => descending ? purchases.OrderByDescending(x => x.Item.Quantity) : purchases.OrderBy(x => x.Item.Quantity),
			"totalAmount" => descending ? purchases.OrderByDescending(x => x.Item.TotalAmount) : purchases.OrderBy(x => x.Item.TotalAmount),
			_ => throw new ArgumentOutOfRangeException(nameof(options), "Unsupported purchase sort column."),
		};
		var rows = await ordered
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

	private static string LiteralPattern(string value)
		=> "%" + value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
