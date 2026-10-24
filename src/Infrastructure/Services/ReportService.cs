using Application.Interfaces.Services;
using Application.Models.Reports;
using Common;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class ReportService(IDbContextFactory<ApplicationDbContext> contextFactory) : IReportService
{
	public async Task<OutOfBalanceResult> GetOutOfBalanceAsync(
		string sortBy,
		string sortDirection,
		int page,
		int pageSize,
		CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		// Build the base query: JOIN receipts with aggregated items, transactions, and adjustments.
		// Use LEFT JOINs so receipts with no items/transactions/adjustments still appear if out of balance.
		var baseQuery = from r in context.Receipts.AsNoTracking()
						where r.DeletedAt == null
						let itemSubtotal = context.ReceiptItems
							.Where(ri => ri.ReceiptId == r.Id && ri.DeletedAt == null)
							.Sum(ri => (decimal?)ri.TotalAmount) ?? 0m
						let adjustmentTotal = context.Adjustments
							.Where(a => a.ReceiptId == r.Id && a.DeletedAt == null)
							.Sum(a => (decimal?)a.Amount) ?? 0m
						let transactionTotal = context.Transactions
							.Where(t => t.ReceiptId == r.Id && t.DeletedAt == null)
							.Sum(t => (decimal?)t.Amount) ?? 0m
						let expectedTotal = itemSubtotal + r.TaxAmount + adjustmentTotal
						let difference = expectedTotal - transactionTotal
						where difference != 0m
						select new
						{
							r.Id,
							r.Location,
							r.Date,
							ItemSubtotal = itemSubtotal,
							TaxAmount = r.TaxAmount,
							AdjustmentTotal = adjustmentTotal,
							ExpectedTotal = expectedTotal,
							TransactionTotal = transactionTotal,
							Difference = difference
						};

		// Count and total-discrepancy are computed in SQL over the filtered set (COUNT / SUM(ABS)),
		// not by materializing every out-of-balance row into memory (RECEIPTS-791).
		int totalCount = await baseQuery.CountAsync(cancellationToken);
		decimal totalDiscrepancy = totalCount == 0
			? 0m
			: await baseQuery.SumAsync(x => Math.Abs(x.Difference), cancellationToken);

		// Sort in SQL: every sort key is a plain/computed column EF can translate to ORDER BY.
		var sortedQuery = (sortBy.ToLowerInvariant(), sortDirection.ToLowerInvariant()) switch
		{
			("difference", "asc") => baseQuery.OrderBy(x => x.Difference),
			("difference", "desc") => baseQuery.OrderByDescending(x => x.Difference),
			("date", "desc") => baseQuery.OrderByDescending(x => x.Date),
			_ => baseQuery.OrderBy(x => x.Date), // default: date asc
		};

		// Deterministic total order (RECEIPTS-791 follow-up): the primary keys above (Difference,
		// Date) are non-unique, so append the unique receipt Id ascending as a tiebreaker regardless
		// of primary direction. Without it, offset pagination over tied rows can skip or repeat a row
		// between page requests — the same determinism fix RECEIPTS-768 applied to the repositories.
		sortedQuery = sortedQuery.ThenBy(x => x.Id);

		// Skip/Take BEFORE materializing — the database paginates, only one page crosses the wire.
		List<OutOfBalanceItem> pagedItems = await sortedQuery
			.Skip((page - 1) * pageSize)
			.Take(pageSize)
			.Select(x => new OutOfBalanceItem(
				x.Id,
				x.Location,
				x.Date,
				x.ItemSubtotal,
				x.TaxAmount,
				x.AdjustmentTotal,
				x.ExpectedTotal,
				x.TransactionTotal,
				x.Difference))
			.ToListAsync(cancellationToken);

		return new OutOfBalanceResult(pagedItems, totalCount, totalDiscrepancy);
	}

	public async Task<SpendingByLocationResult> GetSpendingByLocationAsync(
		DateOnly? startDate,
		DateOnly? endDate,
		string sortBy,
		string sortDirection,
		int page,
		int pageSize,
		CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		var receiptsQuery = context.Receipts.AsNoTracking()
			.Where(r => r.DeletedAt == null);

		if (startDate.HasValue)
		{
			receiptsQuery = receiptsQuery.Where(r => r.Date >= startDate.Value);
		}

		if (endDate.HasValue)
		{
			receiptsQuery = receiptsQuery.Where(r => r.Date <= endDate.Value);
		}

		var baseQuery = from r in receiptsQuery
						let transactionTotal = context.Transactions
							.Where(t => t.ReceiptId == r.Id && t.DeletedAt == null)
							.Sum(t => (decimal?)t.Amount) ?? 0m
						group new { TransactionTotal = transactionTotal } by (r.Location ?? "") into g
						select new
						{
							Location = g.Key == "" ? "(No Location)" : g.Key,
							Visits = g.Count(),
							Total = g.Sum(x => x.TransactionTotal),
						};

		// Count (number of location groups) and grand total are aggregated in SQL over the grouped
		// query, not by pulling every group into memory (RECEIPTS-791).
		int totalCount = await baseQuery.CountAsync(cancellationToken);
		decimal grandTotal = totalCount == 0
			? 0m
			: await baseQuery.SumAsync(x => x.Total, cancellationToken);

		// Sort in SQL: every sort key (including the average = Total / Visits expression) is
		// EF-translatable to ORDER BY. Visits is a group COUNT and therefore always >= 1.
		var sortedQuery = (sortBy.ToLowerInvariant(), sortDirection.ToLowerInvariant()) switch
		{
			("location", "asc") => baseQuery.OrderBy(x => x.Location),
			("location", "desc") => baseQuery.OrderByDescending(x => x.Location),
			("visits", "asc") => baseQuery.OrderBy(x => x.Visits),
			("visits", "desc") => baseQuery.OrderByDescending(x => x.Visits),
			("averagepervisit", "asc") => baseQuery.OrderBy(x => x.Visits > 0 ? x.Total / x.Visits : 0),
			("averagepervisit", "desc") => baseQuery.OrderByDescending(x => x.Visits > 0 ? x.Total / x.Visits : 0),
			("total", "asc") => baseQuery.OrderBy(x => x.Total),
			_ => baseQuery.OrderByDescending(x => x.Total), // default: total desc
		};

		// Deterministic total order (RECEIPTS-791 follow-up): the query GROUPs BY location, so the
		// Location value is unique per row — the two location sorts are already a total order. The
		// measure sorts (visits/total/averagepervisit) are non-unique, so append the unique group key
		// (Location) ascending as a tiebreaker to keep offset pagination stable across page requests.
		sortedQuery = sortedQuery.ThenBy(x => x.Location);

		// Skip/Take BEFORE materializing — only the requested page is fetched.
		List<SpendingByLocationItem> pagedItems = await sortedQuery
			.Skip((page - 1) * pageSize)
			.Take(pageSize)
			.Select(x => new SpendingByLocationItem(
				x.Location,
				x.Visits,
				x.Total,
				x.Visits > 0 ? Math.Round(x.Total / x.Visits, 2, MidpointRounding.AwayFromZero) : 0m))
			.ToListAsync(cancellationToken);

		return new SpendingByLocationResult(pagedItems, totalCount, grandTotal);
	}

	public async Task<ItemDescriptionResult> GetItemDescriptionsAsync(
		string search,
		bool categoryOnly,
		int limit,
		CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		string searchLower = search.ToLower();

		if (categoryOnly)
		{
			var categoryResults = await context.ReceiptItems
				.AsNoTracking()
				.Where(ri => ri.DeletedAt == null && ri.Category.ToLower().Contains(searchLower))
				.GroupBy(ri => ri.Category)
				.Select(g => new { Category = g.Key, Count = g.Count() })
				.OrderByDescending(x => x.Count)
				.Take(limit)
				.ToListAsync(cancellationToken);

			List<ItemDescriptionItem> categories = categoryResults
				.Select(x => new ItemDescriptionItem(x.Category, x.Category, x.Count))
				.ToList();

			return new ItemDescriptionResult(categories);
		}

		var results = await context.ReceiptItems
			.AsNoTracking()
			.Where(ri => ri.DeletedAt == null && ri.Description.ToLower().Contains(searchLower))
			.GroupBy(ri => new { ri.Description, ri.Category })
			.Select(g => new { g.Key.Description, g.Key.Category, Count = g.Count() })
			.OrderByDescending(x => x.Count)
			.Take(limit)
			.ToListAsync(cancellationToken);

		List<ItemDescriptionItem> items = results
			.Select(x => new ItemDescriptionItem(x.Description, x.Category, x.Count))
			.ToList();

		return new ItemDescriptionResult(items);
	}

	public async Task<ItemCostOverTimeResult> GetItemCostOverTimeAsync(
		string? description,
		string? category,
		DateOnly? startDate,
		DateOnly? endDate,
		string granularity,
		string? normalizedDescription,
		CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		// The normalized-description filter needs the canonical name of the item's linked
		// NormalizedDescription, so it is projected alongside the raw columns (LEFT JOIN — items
		// without a normalized description carry null and simply never match).
		var query = from ri in context.ReceiptItems.AsNoTracking().Where(ri => ri.DeletedAt == null)
					join r in context.Receipts.AsNoTracking().Where(r => r.DeletedAt == null)
						on ri.ReceiptId equals r.Id
					join n in context.NormalizedDescriptions.AsNoTracking()
						on ri.NormalizedDescriptionId equals n.Id into normalizedJoin
					from n in normalizedJoin.DefaultIfEmpty()
					select new
					{
						ri.Description,
						ri.Category,
						ri.TotalAmount,
						ri.Quantity,
						ri.UnitPrice,
						ri.NormalizationReviewState,
						r.Date,
						CanonicalName = n.CanonicalName,
						DisplayLabel = n.DisplayLabel,
					};

		// Precedence mirrors the OpenAPI contract: description > normalizedDescription > category.
		if (!string.IsNullOrEmpty(description))
		{
			string descLower = description.ToLower();
			query = query.Where(x => x.Description.ToLower() == descLower);
		}
		else if (!string.IsNullOrEmpty(normalizedDescription))
		{
			query = query.Where(x => x.NormalizationReviewState == NormalizationReviewState.Verified);
			string canonicalLower = normalizedDescription.ToLower();
			bool hasDisplayedMatch = await context.NormalizedDescriptions.AnyAsync(target => (target.DisplayLabel ?? target.CanonicalName).ToLower() == canonicalLower, cancellationToken);
			query = hasDisplayedMatch
				? query.Where(x => (x.DisplayLabel ?? x.CanonicalName) != null && (x.DisplayLabel ?? x.CanonicalName)!.ToLower() == canonicalLower)
				: query.Where(x => x.CanonicalName != null && x.CanonicalName.ToLower() == canonicalLower);
		}
		else if (!string.IsNullOrEmpty(category))
		{
			string catLower = category.ToLower();
			query = query.Where(x => x.Category.ToLower() == catLower);
		}

		if (startDate.HasValue)
		{
			query = query.Where(x => x.Date >= startDate.Value);
		}

		if (endDate.HasValue)
		{
			query = query.Where(x => x.Date <= endDate.Value);
		}

		List<ItemCostBucket> buckets;

		switch (granularity.ToLowerInvariant())
		{
			case "monthly":
				var monthlyData = await query
					.GroupBy(x => new { x.Date.Year, x.Date.Month })
					.Select(g => new { g.Key.Year, g.Key.Month, Amount = g.Average(x => x.UnitPrice) })
					.OrderBy(x => x.Year).ThenBy(x => x.Month)
					.ToListAsync(cancellationToken);

				buckets = monthlyData
					.Select(x => new ItemCostBucket($"{x.Year}-{x.Month:D2}", x.Amount))
					.ToList();
				break;

			case "yearly":
				var yearlyData = await query
					.GroupBy(x => x.Date.Year)
					.Select(g => new { Year = g.Key, Amount = g.Average(x => x.UnitPrice) })
					.OrderBy(x => x.Year)
					.ToListAsync(cancellationToken);

				buckets = yearlyData
					.Select(x => new ItemCostBucket(x.Year.ToString(), x.Amount))
					.ToList();
				break;

			default: // "exact"
				var exactData = await query
					.Select(x => new { x.Date, x.UnitPrice })
					.OrderBy(x => x.Date)
					.ToListAsync(cancellationToken);

				buckets = exactData
					.Select(x => new ItemCostBucket(x.Date.ToString("yyyy-MM-dd"), x.UnitPrice))
					.ToList();
				break;
		}

		return new ItemCostOverTimeResult(buckets);
	}

	public async Task<CategoryTrendsResult> GetCategoryTrendsAsync(
		DateOnly startDate,
		DateOnly endDate,
		string granularity,
		int topN,
		CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		// Join receipt items with receipts for date filtering
		var itemsInRange = context.ReceiptItems
			.AsNoTracking()
			.Where(ri => ri.DeletedAt == null)
			.Join(
				context.Receipts.AsNoTracking().Where(r => r.DeletedAt == null && r.Date >= startDate && r.Date <= endDate),
				ri => ri.ReceiptId,
				r => r.Id,
				(ri, r) => new { ri.Category, ri.TotalAmount, r.Date });

		// Materialize for in-memory grouping (bounded by item count in date range)
		var materialized = await itemsInRange.ToListAsync(cancellationToken);

		if (materialized.Count == 0)
		{
			return new CategoryTrendsResult([], []);
		}

		// Find top-N categories by total spending
		var categoryTotals = materialized
			.GroupBy(x => x.Category)
			.Select(g => new { Category = g.Key, Total = g.Sum(x => x.TotalAmount) })
			.OrderByDescending(x => x.Total)
			.ToList();

		HashSet<string> topCategories = categoryTotals
			.Take(topN)
			.Select(x => x.Category)
			.ToHashSet();

		bool hasOther = categoryTotals.Count > topN;

		// Build ordered category list
		List<string> categories = categoryTotals
			.Take(topN)
			.Select(x => x.Category)
			.ToList();

		if (hasOther)
		{
			categories.Add("Other");
		}

		// Map items to resolved category (top-N or "Other")
		var resolvedItems = materialized.Select(x => new
		{
			Category = topCategories.Contains(x.Category) ? x.Category : "Other",
			x.TotalAmount,
			x.Date
		});

		// Generate all periods in range
		List<string> allPeriods = GeneratePeriods(startDate, endDate, granularity);

		// Group by period and category
		var grouped = resolvedItems
			.GroupBy(x => new { Period = FormatPeriod(x.Date, granularity), x.Category })
			.ToDictionary(g => (g.Key.Period, g.Key.Category), g => g.Sum(x => x.TotalAmount));

		// Build dense zero-filled buckets
		List<CategoryTrendsBucketResult> buckets = allPeriods.Select(period =>
		{
			List<decimal> amounts = categories.Select(cat =>
				grouped.TryGetValue((period, cat), out decimal amount) ? amount : 0m
			).ToList();
			return new CategoryTrendsBucketResult(period, amounts);
		}).ToList();

		return new CategoryTrendsResult(categories, buckets);
	}

	private static string FormatPeriod(DateOnly date, string granularity)
	{
		return granularity.ToLowerInvariant() switch
		{
			"daily" => date.ToString("yyyy-MM-dd"),
			"quarterly" => $"{date.Year} Q{(date.Month - 1) / 3 + 1}",
			"yearly" => date.Year.ToString(),
			_ => $"{date.Year}-{date.Month:D2}" // monthly
		};
	}

	private static List<string> GeneratePeriods(DateOnly start, DateOnly end, string granularity)
	{
		List<string> periods = [];
		DateOnly current = granularity.ToLowerInvariant() switch
		{
			"daily" => start,
			"quarterly" => new DateOnly(start.Year, ((start.Month - 1) / 3) * 3 + 1, 1),
			"yearly" => new DateOnly(start.Year, 1, 1),
			_ => new DateOnly(start.Year, start.Month, 1) // monthly
		};

		while (current <= end)
		{
			periods.Add(FormatPeriod(current, granularity));
			current = granularity.ToLowerInvariant() switch
			{
				"daily" => current.AddDays(1),
				"quarterly" => current.AddMonths(3),
				"yearly" => current.AddYears(1),
				_ => current.AddMonths(1) // monthly
			};
		}

		return periods;
	}

	/// <summary>
	/// Label for receipt items with no linked normalized description. Materialized into the
	/// GROUP BY key via COALESCE so the synthetic bucket sorts and paginates alongside real
	/// canonical names instead of being a NULL that Postgres orders at one end.
	/// </summary>
	private const string NotNormalizedLabel = "(Not Normalized)";

	public async Task<SpendingByNormalizedDescriptionResult> GetSpendingByNormalizedDescriptionAsync(
		DateTimeOffset? from,
		DateTimeOffset? to,
		string sortBy,
		string sortDirection,
		int page,
		int pageSize,
		CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		// Receipt.Date is DateOnly; convert the caller-supplied DateTimeOffset bounds to DateOnly
		// using the UTC day so filtering is deterministic regardless of the caller's offset.
		DateOnly? fromDate = from.HasValue ? DateOnly.FromDateTime(from.Value.UtcDateTime) : null;
		DateOnly? toDate = to.HasValue ? DateOnly.FromDateTime(to.Value.UtcDateTime) : null;

		var receiptsQuery = context.Receipts.AsNoTracking()
			.Where(r => r.DeletedAt == null);

		if (fromDate.HasValue)
		{
			receiptsQuery = receiptsQuery.Where(r => r.Date >= fromDate.Value);
		}

		if (toDate.HasValue)
		{
			receiptsQuery = receiptsQuery.Where(r => r.Date <= toDate.Value);
		}

		// Persisted review state is maintained by the shared evidence policy and database guards.
		// A label is presentation, never identity: distinct verified products may share one.
		var assignments = from ri in context.ReceiptItems.AsNoTracking().Where(ri => ri.DeletedAt == null)
						  join r in receiptsQuery on ri.ReceiptId equals r.Id
						  join n in context.NormalizedDescriptions.AsNoTracking() on ri.NormalizedDescriptionId equals n.Id into gj
						  from n in gj.DefaultIfEmpty()
						  let verified = ri.NormalizationReviewState == NormalizationReviewState.Verified && n != null && n.Status == NormalizedDescriptionStatus.Active
						  select new
						  {
							  Id = verified ? (Guid?)n.Id : null,
							  Name = verified ? n.DisplayLabel ?? n.CanonicalName : ri.NormalizationReviewState == NormalizationReviewState.Excluded ? "(Excluded)" : "(Needs review)",
							  State = verified ? "verified" : ri.NormalizationReviewState == NormalizationReviewState.Excluded ? "excluded" : "needsReview",
							  ri.TotalAmount,
							  ri.TotalAmountCurrency,
							  r.Date,
						  };
		var baseQuery = from a in assignments
						group a by new { a.Id, a.Name, a.State } into g
						select new
						{
							g.Key.Id,
							CanonicalName = g.Key.Name,
							g.Key.State,
							Total = g.Sum(x => x.TotalAmount),
							ItemCount = g.Count(),
							FirstSeen = g.Min(x => x.Date),
							LastSeen = g.Max(x => x.Date),
						};

		// Count (number of buckets) and grand total are aggregated in SQL over the grouped query.
		// GrandTotal is the denominator the client uses for each row's share-of-total, so it must
		// span every bucket, not just the requested page.
		int totalCount = await baseQuery.CountAsync(cancellationToken);
		decimal grandTotal = totalCount == 0
			? 0m
			: await baseQuery.SumAsync(x => x.Total, cancellationToken);

		var sortedQuery = (sortBy.ToLowerInvariant(), sortDirection.ToLowerInvariant()) switch
		{
			("canonicalname", "asc") => baseQuery.OrderBy(x => x.CanonicalName),
			("canonicalname", "desc") => baseQuery.OrderByDescending(x => x.CanonicalName),
			("itemcount", "asc") => baseQuery.OrderBy(x => x.ItemCount),
			("itemcount", "desc") => baseQuery.OrderByDescending(x => x.ItemCount),
			("totalamount", "asc") => baseQuery.OrderBy(x => x.Total),
			_ => baseQuery.OrderByDescending(x => x.Total), // default: totalAmount desc
		};

		// Deterministic total order (same rule as the other paginated reports): the GROUP BY key is
		// unique per row, so appending it ascending turns the non-unique measure sorts into a total
		// order and keeps offset pagination from skipping or repeating rows between page requests.
		sortedQuery = sortedQuery.ThenBy(x => x.CanonicalName).ThenBy(x => x.Id).ThenBy(x => x.State);

		// Skip/Take BEFORE materializing — the database paginates, only one page crosses the wire.
		var pagedGroups = await sortedQuery
			.Skip((page - 1) * pageSize)
			.Take(pageSize)
			.ToListAsync(cancellationToken);

		if (pagedGroups.Count == 0)
		{
			return new SpendingByNormalizedDescriptionResult([], totalCount, grandTotal, from, to);
		}

		Guid[] pageIds = [.. pagedGroups.Where(x => x.Id.HasValue).Select(x => x.Id!.Value)];
		string[] pageStates = [.. pagedGroups.Where(x => !x.Id.HasValue).Select(x => x.State)];
		var currencyCounts = await assignments.Where(x => (x.Id.HasValue && pageIds.Contains(x.Id.Value)) || (!x.Id.HasValue && pageStates.Contains(x.State)))
			.GroupBy(x => new { x.Id, x.State, x.TotalAmountCurrency })
			.Select(g => new { g.Key.Id, g.Key.State, g.Key.TotalAmountCurrency, Count = g.Count() })
			.ToListAsync(cancellationToken);
		List<SpendingByNormalizedDescriptionItem> items = [.. pagedGroups.Select(x =>
			new SpendingByNormalizedDescriptionItem(x.CanonicalName, x.Total,
				currencyCounts.Where(c => c.Id == x.Id && c.State == x.State).OrderByDescending(c => c.Count).ThenBy(c => c.TotalAmountCurrency).First().TotalAmountCurrency.ToString(),
				x.ItemCount, ToDateTimeOffset(x.FirstSeen), ToDateTimeOffset(x.LastSeen),
				x.State == "verified" ? NormalizedDescriptionStatus.Active : null, x.Id, x.State))];

		return new SpendingByNormalizedDescriptionResult(items, totalCount, grandTotal, from, to);
	}

	private static DateTimeOffset ToDateTimeOffset(DateOnly date) =>
		new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

	public async Task<UncategorizedItemsResult> GetUncategorizedItemsAsync(
		string sortBy,
		string sortDirection,
		int page,
		int pageSize,
		CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		var baseQuery = from ri in context.ReceiptItems.AsNoTracking()
						where ri.DeletedAt == null && ri.Category == "Uncategorized"
						select new
						{
							ri.Id,
							ri.ReceiptId,
							ri.ReceiptItemCode,
							ri.Description,
							ri.Quantity,
							ri.UnitPrice,
							ri.TotalAmount,
							ri.Category,
							ri.Subcategory
						};

		// Count in SQL over the filtered set instead of materializing every uncategorized item
		// just to read its length (RECEIPTS-791).
		int totalCount = await baseQuery.CountAsync(cancellationToken);

		// Sort in SQL: all keys are plain columns (ReceiptItemCode uses COALESCE for its null case).
		var sortedQuery = (sortBy.ToLowerInvariant(), sortDirection.ToLowerInvariant()) switch
		{
			("total", "asc") => baseQuery.OrderBy(x => x.TotalAmount),
			("total", "desc") => baseQuery.OrderByDescending(x => x.TotalAmount),
			("itemcode", "asc") => baseQuery.OrderBy(x => x.ReceiptItemCode ?? string.Empty),
			("itemcode", "desc") => baseQuery.OrderByDescending(x => x.ReceiptItemCode ?? string.Empty),
			("description", "desc") => baseQuery.OrderByDescending(x => x.Description),
			_ => baseQuery.OrderBy(x => x.Description),
		};

		// Deterministic total order (RECEIPTS-791 follow-up): the primary keys above (TotalAmount,
		// ReceiptItemCode, Description) are non-unique, so append the unique receipt-item Id ascending
		// as a tiebreaker regardless of primary direction, keeping offset pagination stable across
		// page requests.
		sortedQuery = sortedQuery.ThenBy(x => x.Id);

		// Skip/Take BEFORE materializing — only the requested page is fetched.
		List<UncategorizedItemRecord> pagedItems = await sortedQuery
			.Skip((page - 1) * pageSize)
			.Take(pageSize)
			.Select(x => new UncategorizedItemRecord(
				x.Id,
				x.ReceiptId,
				x.ReceiptItemCode,
				x.Description,
				x.Quantity,
				x.UnitPrice,
				x.TotalAmount,
				x.Category,
				x.Subcategory))
			.ToListAsync(cancellationToken);

		return new UncategorizedItemsResult(pagedItems, totalCount);
	}

	public async Task<ReportsHealthSummaryResult> GetHealthSummaryAsync(CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		// Every count below is a SQL COUNT over a filtered/grouped set — no report rows are
		// materialized. The reports hub polls this on every visit, so it must stay cheap
		// (RECEIPTS-839).

		// Mirrors the GetOutOfBalanceAsync predicate: expected (items + tax + adjustments)
		// != recorded transaction total.
		int outOfBalanceCount = await (from r in context.Receipts.AsNoTracking()
									   where r.DeletedAt == null
									   let itemSubtotal = context.ReceiptItems
										   .Where(ri => ri.ReceiptId == r.Id && ri.DeletedAt == null)
										   .Sum(ri => (decimal?)ri.TotalAmount) ?? 0m
									   let adjustmentTotal = context.Adjustments
										   .Where(a => a.ReceiptId == r.Id && a.DeletedAt == null)
										   .Sum(a => (decimal?)a.Amount) ?? 0m
									   let transactionTotal = context.Transactions
										   .Where(t => t.ReceiptId == r.Id && t.DeletedAt == null)
										   .Sum(t => (decimal?)t.Amount) ?? 0m
									   where itemSubtotal + r.TaxAmount + adjustmentTotal != transactionTotal
									   select r.Id)
			.CountAsync(cancellationToken);

		int duplicateGroupCount = await DuplicateReportReader.CountUnacceptedDefaultGroupsAsync(context, cancellationToken);

		int uncategorizedItemCount = await context.ReceiptItems.AsNoTracking()
			.Where(ri => ri.DeletedAt == null && ri.Category == "Uncategorized")
			.CountAsync(cancellationToken);

		return new ReportsHealthSummaryResult(
			outOfBalanceCount,
			duplicateGroupCount,
			uncategorizedItemCount);
	}
}
