using System.Text.RegularExpressions;
using Application.Interfaces.Services;
using Application.Models.Reports;
using Common;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public partial class ReportService(IDbContextFactory<ApplicationDbContext> contextFactory) : IReportService
{
	[GeneratedRegex(@"\s+")]
	private static partial Regex WhitespaceRegex();
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
		CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		var query = context.ReceiptItems
			.AsNoTracking()
			.Where(ri => ri.DeletedAt == null)
			.Join(
				context.Receipts.AsNoTracking().Where(r => r.DeletedAt == null),
				ri => ri.ReceiptId,
				r => r.Id,
				(ri, r) => new { ri.Description, ri.Category, ri.TotalAmount, ri.Quantity, ri.UnitPrice, r.Date });

		if (!string.IsNullOrEmpty(description))
		{
			string descLower = description.ToLower();
			query = query.Where(x => x.Description.ToLower() == descLower);
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

	public async Task<DuplicateDetectionResult> GetDuplicatesAsync(
		string matchOn,
		string locationTolerance,
		decimal totalTolerance,
		bool includeAccepted,
		CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		List<ReceiptSnapshot> receipts = await (from r in context.Receipts.AsNoTracking()
												where r.DeletedAt == null
												let transactionTotal = context.Transactions
													.Where(t => t.ReceiptId == r.Id && t.DeletedAt == null)
													.Sum(t => (decimal?)t.Amount) ?? 0m
												select new ReceiptSnapshot(
													r.Id,
													r.Location,
													r.Date,
													transactionTotal
												)).ToListAsync(cancellationToken);

		bool normalized = locationTolerance.Equals("normalized", StringComparison.OrdinalIgnoreCase);

		string NormalizeLocation(string location)
		{
			string trimmed = location.Trim().ToLowerInvariant();
			return WhitespaceRegex().Replace(trimmed, " ");
		}

		bool TotalsMatch(decimal a, decimal b) => Math.Abs(a - b) <= totalTolerance;

		List<DuplicateGroup> groups = matchOn.ToLowerInvariant() switch
		{
			"dateandlocation" => receipts
				.GroupBy(r => (r.Date, Location: normalized ? NormalizeLocation(r.Location) : r.Location))
				.Where(g => g.Count() > 1)
				.Select(g => new DuplicateGroup(
					$"{g.Key.Date:yyyy-MM-dd} @ {g.First().Location}",
					g.Select(ToSummary).ToList()))
				.ToList(),

			"dateandtotal" => ClusterByTotal(
				receipts.GroupBy(r => r.Date),
				TotalsMatch,
				dateGroup => dateGroup.Key,
				(date, seed) => $"{date:yyyy-MM-dd} — ${seed.TransactionTotal:F2}"),

			_ => ClusterByTotal(
				receipts.GroupBy(r => (r.Date, Location: normalized ? NormalizeLocation(r.Location) : r.Location)),
				TotalsMatch,
				locDateGroup => locDateGroup.Key,
				(key, seed) => $"{key.Date:yyyy-MM-dd} @ {seed.Location} — ${seed.TransactionTotal:F2}")
		};

		// Suppression is applied AFTER clustering, on receipt identities, so it is unaffected by the
		// tolerance / normalization settings that shaped the clusters above (RECEIPTS-834).
		HashSet<(Guid A, Guid B)> acceptedPairs = await LoadAcceptedPairsAsync(context, cancellationToken);

		List<DuplicateGroup> visibleGroups = [];
		foreach (DuplicateGroup group in groups)
		{
			bool isAccepted = IsFullyAccepted(group.Receipts.Select(r => r.ReceiptId), acceptedPairs);
			if (isAccepted && !includeAccepted)
			{
				continue;
			}

			visibleGroups.Add(group with { IsAccepted = isAccepted });
		}

		int totalDuplicateReceipts = visibleGroups.Sum(g => g.Receipts.Count);
		return new DuplicateDetectionResult(visibleGroups, visibleGroups.Count, totalDuplicateReceipts);
	}

	public async Task<int> AcceptDuplicateGroupAsync(
		List<Guid> receiptIds,
		CancellationToken cancellationToken)
	{
		List<Guid> distinctIds = [.. receiptIds.Distinct()];
		if (distinctIds.Count < 2)
		{
			return 0;
		}

		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		HashSet<Guid> existingReceipts = [.. await context.Receipts
			.AsNoTracking()
			.Where(r => distinctIds.Contains(r.Id) && r.DeletedAt == null)
			.Select(r => r.Id)
			.ToListAsync(cancellationToken)];

		List<Guid> missing = [.. distinctIds.Where(id => !existingReceipts.Contains(id))];
		if (missing.Count > 0)
		{
			throw new KeyNotFoundException(
				$"Receipt(s) not found: {string.Join(", ", missing)}");
		}

		List<(Guid A, Guid B)> pairs = [.. CanonicalPairs(distinctIds)];
		HashSet<Guid> idSet = [.. distinctIds];

		// IgnoreQueryFilters so a previously un-accepted (soft-deleted) pair is restored instead of
		// colliding with the tombstone the filtered unique index still allows alongside a new row.
		List<AcceptedDuplicatePairEntity> known = await context.AcceptedDuplicatePairs
			.IgnoreQueryFilters()
			.Where(p => idSet.Contains(p.ReceiptIdA) && idSet.Contains(p.ReceiptIdB))
			.ToListAsync(cancellationToken);

		Dictionary<(Guid, Guid), AcceptedDuplicatePairEntity> activeByPair = known
			.Where(p => p.DeletedAt == null)
			.ToDictionary(p => (p.ReceiptIdA, p.ReceiptIdB));

		Dictionary<(Guid, Guid), AcceptedDuplicatePairEntity> tombstoneByPair = known
			.Where(p => p.DeletedAt != null)
			.GroupBy(p => (p.ReceiptIdA, p.ReceiptIdB))
			.ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.DeletedAt).First());

		DateTimeOffset now = DateTimeOffset.UtcNow;
		int accepted = 0;

		foreach ((Guid a, Guid b) in pairs)
		{
			if (activeByPair.ContainsKey((a, b)))
			{
				continue;
			}

			if (tombstoneByPair.TryGetValue((a, b), out AcceptedDuplicatePairEntity? tombstone))
			{
				tombstone.DeletedAt = null;
				tombstone.DeletedByUserId = null;
				tombstone.DeletedByApiKeyId = null;
				tombstone.CascadeDeletedByParentId = null;
				tombstone.AcceptedAt = now;
			}
			else
			{
				context.AcceptedDuplicatePairs.Add(new AcceptedDuplicatePairEntity
				{
					Id = Guid.NewGuid(),
					ReceiptIdA = a,
					ReceiptIdB = b,
					AcceptedAt = now
				});
			}

			accepted++;
		}

		if (accepted > 0)
		{
			await context.SaveChangesAsync(cancellationToken);
		}

		return accepted;
	}

	public async Task<int> UnacceptDuplicateGroupAsync(
		List<Guid> receiptIds,
		CancellationToken cancellationToken)
	{
		HashSet<Guid> idSet = [.. receiptIds];
		if (idSet.Count < 2)
		{
			return 0;
		}

		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		List<AcceptedDuplicatePairEntity> toRemove = await context.AcceptedDuplicatePairs
			.Where(p => idSet.Contains(p.ReceiptIdA) && idSet.Contains(p.ReceiptIdB))
			.ToListAsync(cancellationToken);

		if (toRemove.Count == 0)
		{
			return 0;
		}

		// RemoveRange is converted to a soft delete by ApplicationDbContext.HandleSoftDelete, which
		// also stamps the deleting user and emits the audit-log entry.
		context.AcceptedDuplicatePairs.RemoveRange(toRemove);
		await context.SaveChangesAsync(cancellationToken);

		return toRemove.Count;
	}

	public async Task<AcceptedDuplicatesResult> GetAcceptedDuplicatesAsync(CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		var pairRows = await context.AcceptedDuplicatePairs
			.AsNoTracking()
			.Select(p => new { p.ReceiptIdA, p.ReceiptIdB, p.AcceptedAt })
			.ToListAsync(cancellationToken);

		List<AcceptedPairSnapshot> pairs = [.. pairRows
			.Select(p => new AcceptedPairSnapshot(p.ReceiptIdA, p.ReceiptIdB, p.AcceptedAt))];

		if (pairs.Count == 0)
		{
			return new AcceptedDuplicatesResult([], 0);
		}

		// Connected components of the acceptance graph. Two acceptances that share a receipt merge
		// into one displayed group — a deliberate simplification of the pairwise model.
		Dictionary<Guid, Guid> parent = [];
		foreach (AcceptedPairSnapshot pair in pairs)
		{
			Union(parent, pair.ReceiptIdA, pair.ReceiptIdB);
		}

		Dictionary<Guid, List<Guid>> componentMembers = [];
		Dictionary<Guid, DateTimeOffset> componentAcceptedAt = [];
		foreach (AcceptedPairSnapshot pair in pairs)
		{
			Guid root = Find(parent, pair.ReceiptIdA);

			if (!componentMembers.TryGetValue(root, out List<Guid>? members))
			{
				members = [];
				componentMembers[root] = members;
			}

			if (!members.Contains(pair.ReceiptIdA))
			{
				members.Add(pair.ReceiptIdA);
			}

			if (!members.Contains(pair.ReceiptIdB))
			{
				members.Add(pair.ReceiptIdB);
			}

			// The component is stamped with its most recent acceptance.
			if (!componentAcceptedAt.TryGetValue(root, out DateTimeOffset acceptedAt) || pair.AcceptedAt > acceptedAt)
			{
				componentAcceptedAt[root] = pair.AcceptedAt;
			}
		}

		HashSet<Guid> allMembers = [.. componentMembers.Values.SelectMany(m => m)];

		Dictionary<Guid, DuplicateReceiptSummary> summaries = await (
			from r in context.Receipts.AsNoTracking()
			where r.DeletedAt == null && allMembers.Contains(r.Id)
			let transactionTotal = context.Transactions
				.Where(t => t.ReceiptId == r.Id && t.DeletedAt == null)
				.Sum(t => (decimal?)t.Amount) ?? 0m
			select new DuplicateReceiptSummary(r.Id, r.Location, r.Date, transactionTotal))
			.ToDictionaryAsync(s => s.ReceiptId, cancellationToken);

		List<AcceptedDuplicateGroup> groups = [];
		foreach ((Guid root, List<Guid> members) in componentMembers)
		{
			// Members whose receipt is soft-deleted or purged are dropped: there is nothing to show
			// and nothing left to warn about. A component with fewer than two surviving receipts can
			// never produce a duplicate warning, so it is not listed either. Restoring the receipt
			// brings both the group and its (still-stored) acceptance back.
			List<DuplicateReceiptSummary> receipts = [.. members
				.Where(summaries.ContainsKey)
				.Select(id => summaries[id])
				.OrderBy(r => r.Date)
				.ThenBy(r => r.Location, StringComparer.Ordinal)];

			if (receipts.Count < 2)
			{
				continue;
			}

			groups.Add(new AcceptedDuplicateGroup(receipts, componentAcceptedAt[root]));
		}

		groups = [.. groups.OrderByDescending(g => g.AcceptedAt)];
		return new AcceptedDuplicatesResult(groups, groups.Count);
	}

	private static async Task<HashSet<(Guid A, Guid B)>> LoadAcceptedPairsAsync(
		ApplicationDbContext context,
		CancellationToken cancellationToken)
	{
		var rows = await context.AcceptedDuplicatePairs
			.AsNoTracking()
			.Select(p => new { p.ReceiptIdA, p.ReceiptIdB })
			.ToListAsync(cancellationToken);

		return [.. rows.Select(r => (r.ReceiptIdA, r.ReceiptIdB))];
	}

	/// <summary>
	/// True when EVERY unordered pair within the group has been accepted. Requiring the full pair set
	/// is what makes a group that gains a new member resurface: the newcomer's pairs are undismissed.
	/// </summary>
	private static bool IsFullyAccepted(IEnumerable<Guid> receiptIds, HashSet<(Guid A, Guid B)> acceptedPairs)
	{
		if (acceptedPairs.Count == 0)
		{
			return false;
		}

		List<Guid> ids = [.. receiptIds.Distinct()];
		if (ids.Count < 2)
		{
			return false;
		}

		return CanonicalPairs(ids).All(acceptedPairs.Contains);
	}

	/// <summary>Every unordered pair of the supplied IDs, each ordered so A &lt; B.</summary>
	private static IEnumerable<(Guid A, Guid B)> CanonicalPairs(List<Guid> ids)
	{
		for (int i = 0; i < ids.Count; i++)
		{
			for (int j = i + 1; j < ids.Count; j++)
			{
				yield return ids[i].CompareTo(ids[j]) < 0 ? (ids[i], ids[j]) : (ids[j], ids[i]);
			}
		}
	}

	private static Guid Find(Dictionary<Guid, Guid> parent, Guid id)
	{
		if (!parent.TryGetValue(id, out Guid value))
		{
			parent[id] = id;
			return id;
		}

		if (value == id)
		{
			return id;
		}

		Guid root = Find(parent, value);
		parent[id] = root;
		return root;
	}

	private static void Union(Dictionary<Guid, Guid> parent, Guid left, Guid right)
	{
		Guid leftRoot = Find(parent, left);
		Guid rightRoot = Find(parent, right);
		if (leftRoot != rightRoot)
		{
			parent[rightRoot] = leftRoot;
		}
	}

	private sealed record AcceptedPairSnapshot(Guid ReceiptIdA, Guid ReceiptIdB, DateTimeOffset AcceptedAt);

	private static List<DuplicateGroup> ClusterByTotal<TKey>(
		IEnumerable<IGrouping<TKey, ReceiptSnapshot>> groupedReceipts,
		Func<decimal, decimal, bool> totalsMatch,
		Func<IGrouping<TKey, ReceiptSnapshot>, TKey> keySelector,
		Func<TKey, ReceiptSnapshot, string> formatMatchKey)
	{
		List<DuplicateGroup> result = [];

		foreach (IGrouping<TKey, ReceiptSnapshot> group in groupedReceipts)
		{
			List<ReceiptSnapshot> remaining = [.. group];
			TKey key = keySelector(group);

			while (remaining.Count > 0)
			{
				ReceiptSnapshot seed = remaining[0];
				List<ReceiptSnapshot> cluster = [seed];
				remaining.RemoveAt(0);

				for (int i = remaining.Count - 1; i >= 0; i--)
				{
					if (totalsMatch(seed.TransactionTotal, remaining[i].TransactionTotal))
					{
						cluster.Add(remaining[i]);
						remaining.RemoveAt(i);
					}
				}

				if (cluster.Count > 1)
				{
					result.Add(new DuplicateGroup(
						formatMatchKey(key, seed),
						cluster.Select(ToSummary).ToList()));
				}
			}
		}

		return result;
	}

	private static DuplicateReceiptSummary ToSummary(ReceiptSnapshot r) =>
		new(r.Id, r.Location, r.Date, r.TransactionTotal);

	private sealed record ReceiptSnapshot(Guid Id, string Location, DateOnly Date, decimal TransactionTotal);

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

	public async Task<SpendingByNormalizedDescriptionResult> GetSpendingByNormalizedDescriptionAsync(
		DateTimeOffset? from,
		DateTimeOffset? to,
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

		// LEFT JOIN ReceiptItems -> NormalizedDescriptions (via nullable FK).
		// Soft-deleted receipts already excluded above; exclude soft-deleted items here.
		// The join via Receipt.Id enforces the date filter on the item side as well.
		var joined = from ri in context.ReceiptItems.AsNoTracking().Where(ri => ri.DeletedAt == null)
					 join r in receiptsQuery on ri.ReceiptId equals r.Id
					 join n in context.NormalizedDescriptions.AsNoTracking() on ri.NormalizedDescriptionId equals n.Id into gj
					 from n in gj.DefaultIfEmpty()
					 select new
					 {
						 CanonicalName = n != null ? n.CanonicalName : null,
						 ri.TotalAmount,
						 ri.TotalAmountCurrency,
						 r.Date,
					 };

		var materialized = await joined.ToListAsync(cancellationToken);

		// Group by the canonical name; NULL FK buckets into a synthetic "(Not Normalized)" group.
		const string NotNormalizedLabel = "(Not Normalized)";
		List<SpendingByNormalizedDescriptionItem> items = materialized
			.GroupBy(x => x.CanonicalName ?? NotNormalizedLabel)
			.Select(g =>
			{
				decimal total = g.Sum(x => x.TotalAmount);
				int count = g.Count();
				DateOnly minDate = g.Min(x => x.Date);
				DateOnly maxDate = g.Max(x => x.Date);

				// Dominant currency: most common across the bucket. Ties broken by name asc
				// (stable via Currency enum ordering). Empty groups fall back to USD.
				string currency = g
					.GroupBy(x => x.TotalAmountCurrency)
					.OrderByDescending(cg => cg.Count())
					.ThenBy(cg => cg.Key)
					.Select(cg => cg.Key.ToString())
					.FirstOrDefault() ?? Currency.USD.ToString();

				return new SpendingByNormalizedDescriptionItem(
					g.Key,
					total,
					currency,
					count,
					ToDateTimeOffset(minDate),
					ToDateTimeOffset(maxDate));
			})
			.OrderByDescending(x => x.TotalAmount)
			.ThenBy(x => x.CanonicalName)
			.ToList();

		return new SpendingByNormalizedDescriptionResult(items, from, to);
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

		// Matches the duplicate-detection report's default mode (matchOn=dateAndLocation,
		// locationTolerance=exact), which is the only variant expressible as a single GROUP
		// BY ... HAVING. The tolerance-based modes need in-memory clustering and are far too
		// expensive for a badge count.
		int duplicateGroupCount = await context.Receipts.AsNoTracking()
			.Where(r => r.DeletedAt == null)
			.GroupBy(r => new { r.Date, r.Location })
			.Where(g => g.Count() > 1)
			.Select(g => g.Key)
			.CountAsync(cancellationToken);

		int uncategorizedItemCount = await context.ReceiptItems.AsNoTracking()
			.Where(ri => ri.DeletedAt == null && ri.Category == "Uncategorized")
			.CountAsync(cancellationToken);

		return new ReportsHealthSummaryResult(
			outOfBalanceCount,
			duplicateGroupCount,
			uncategorizedItemCount);
	}
}
