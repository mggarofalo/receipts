using System.Text.RegularExpressions;
using Application.Interfaces.Services;
using Application.Models.Reports;
using Common;
using Infrastructure.Entities.Core;
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
						r.Date,
						CanonicalName = n.CanonicalName,
					};

		// Precedence mirrors the OpenAPI contract: description > normalizedDescription > category.
		if (!string.IsNullOrEmpty(description))
		{
			string descLower = description.ToLower();
			query = query.Where(x => x.Description.ToLower() == descLower);
		}
		else if (!string.IsNullOrEmpty(normalizedDescription))
		{
			string canonicalLower = normalizedDescription.ToLower();
			query = query.Where(x => x.CanonicalName != null && x.CanonicalName.ToLower() == canonicalLower);
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
		//
		// Scoped to the receipts that actually landed in a group: an unpredicated read of the whole
		// table made report cost scale with every acceptance in the database, and paid that cost even
		// when clustering produced nothing. Only pairs whose BOTH ends are in a group can suppress
		// one, so anything else is dead weight.
		HashSet<Guid> clusteredReceiptIds = [.. groups.SelectMany(g => g.Receipts).Select(r => r.ReceiptId)];
		HashSet<(Guid A, Guid B)> acceptedPairs = clusteredReceiptIds.Count == 0
			? []
			: await LoadAcceptedPairsAsync(context, clusteredReceiptIds, cancellationToken);

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

	/// <summary>Number of missing receipt IDs echoed back in a not-found message before truncating.</summary>
	private const int MaxReportedMissingIds = 10;

	private const string PostgreSQLProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

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
			throw new KeyNotFoundException(FormatMissingReceiptsMessage(missing));
		}

		List<(Guid A, Guid B)> pairs = [.. CanonicalPairs(distinctIds)];
		DateTimeOffset now = DateTimeOffset.UtcNow;

		return context.Database.ProviderName == PostgreSQLProvider
			? await InsertPairsIgnoringConflictsAsync(context, pairs, now, cancellationToken)
			: await InsertPairsTrackedAsync(context, pairs, now, cancellationToken);
	}

	/// <summary>
	/// Truncates the echoed ID list. The caller returns this verbatim as a 404 body and logs it, so an
	/// unbounded join over every unmatched GUID turns a bad request into a multi-megabyte response and
	/// log line.
	/// </summary>
	private static string FormatMissingReceiptsMessage(List<Guid> missing)
	{
		string sample = string.Join(", ", missing.Take(MaxReportedMissingIds));
		return missing.Count > MaxReportedMissingIds
			? $"Receipt(s) not found: {sample} (+{missing.Count - MaxReportedMissingIds} more)"
			: $"Receipt(s) not found: {sample}";
	}

	/// <summary>
	/// Inserts every pair in one statement, letting the database decide which already exist.
	///
	/// The decision has to happen at write time, not read time. Reading the existing rows first and
	/// skipping the ones that look present loses both ways under concurrency: two accepts racing on
	/// the same pair both insert and one dies on the unique index (23505, rolling back the whole
	/// request including its unrelated pairs), and an unaccept committing between the read and the
	/// write makes the accept report success while leaving the pair un-accepted.
	///
	/// ON CONFLICT DO NOTHING collapses a duplicate INSERT into a no-op, and the affected-row count is
	/// the number genuinely accepted. The conflict target repeats the unique index's predicate so
	/// Postgres can infer the partial index. A tombstone does not conflict — it is outside the partial
	/// index — so re-accepting after an un-accept inserts a fresh row and leaves the un-accept in
	/// history.
	///
	/// ON CONFLICT does NOT, however, collapse a deadlock. Two transactions inserting the same pairs
	/// in opposite orders take the same index-key locks in opposite orders and one dies with 40P01.
	/// That is reachable without adversarial input, because GetDuplicatesAsync returns a group's
	/// members in different orders per matchOn — ClusterByTotal walks its remaining list backwards, so
	/// dateAndLocation yields [r0,r1,r2] where dateAndLocationAndTotal yields [r0,r2,r1]. Sorting the
	/// pairs here gives every caller the same global lock order, which is what actually prevents it.
	/// </summary>
	private static async Task<int> InsertPairsIgnoringConflictsAsync(
		ApplicationDbContext context,
		List<(Guid A, Guid B)> pairs,
		DateTimeOffset acceptedAt,
		CancellationToken cancellationToken)
	{
		List<(Guid A, Guid B)> ordered = [.. pairs.OrderBy(p => p.A).ThenBy(p => p.B)];

		Guid[] ids = [.. ordered.Select(_ => Guid.NewGuid())];
		Guid[] aIds = [.. ordered.Select(p => p.A)];
		Guid[] bIds = [.. ordered.Select(p => p.B)];

		return await context.Database.ExecuteSqlRawAsync(
			"""
			INSERT INTO "receipts"."AcceptedDuplicatePairs" ("Id", "ReceiptIdA", "ReceiptIdB", "AcceptedAt")
			SELECT id, a, b, {3}
			FROM unnest({0}::uuid[], {1}::uuid[], {2}::uuid[]) AS t(id, a, b)
			ON CONFLICT ("ReceiptIdA", "ReceiptIdB") WHERE "DeletedAt" IS NULL DO NOTHING;
			""",
			[ids, aIds, bIds, acceptedAt],
			cancellationToken);
	}

	/// <summary>
	/// Change-tracker fallback for providers without ON CONFLICT (the InMemory provider used by unit
	/// tests). Idempotent for sequential callers, which is all a single-threaded test needs; it cannot
	/// be made race-safe without the database constraint, so production never takes this path.
	/// </summary>
	private static async Task<int> InsertPairsTrackedAsync(
		ApplicationDbContext context,
		List<(Guid A, Guid B)> pairs,
		DateTimeOffset acceptedAt,
		CancellationToken cancellationToken)
	{
		HashSet<Guid> endpoints = [.. pairs.SelectMany(p => new[] { p.A, p.B })];

		HashSet<(Guid, Guid)> alreadyActive = [.. (await context.AcceptedDuplicatePairs
			.Where(p => endpoints.Contains(p.ReceiptIdA) && endpoints.Contains(p.ReceiptIdB))
			.Select(p => new { p.ReceiptIdA, p.ReceiptIdB })
			.ToListAsync(cancellationToken))
			.Select(p => (p.ReceiptIdA, p.ReceiptIdB))];

		int accepted = 0;
		foreach ((Guid a, Guid b) in pairs)
		{
			if (alreadyActive.Contains((a, b)))
			{
				continue;
			}

			context.AcceptedDuplicatePairs.Add(new AcceptedDuplicatePairEntity
			{
				Id = Guid.NewGuid(),
				ReceiptIdA = a,
				ReceiptIdB = b,
				AcceptedAt = acceptedAt
			});
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

		// Remove exactly the pairs among the submitted receipts — nothing wider.
		//
		// An earlier revision expanded to the connected component of the acceptance graph, to fix undo
		// stranding pairs when a group had a soft-deleted member. That over-corrected. The undo button
		// in the accepted-groups list acts on a whole acceptance, but "Report again" in the report acts
		// on a REPORT CLUSTER, and a cluster can be a strict subset of the component: accept {A,B} and
		// {C,D} at tolerance 0, widen tolerance so all four cluster together, accept that, then narrow
		// tolerance again — clicking "Report again" on {A,B} would expand to the component and silently
		// destroy the untouched {C,D} acceptance. Every value in that sequence is a dropdown option.
		//
		// The stranding bug is fixed at its actual source instead: GetAcceptedDuplicatesAsync now
		// reports each group's COMPLETE member set (including soft-deleted receipts) alongside the
		// subset it displays, so the client can submit every member and undo removes every pair. Here,
		// "remove what was asked for and nothing else" is both correct and the only safe rule, because
		// this method cannot tell a full acceptance from a cluster-shaped slice of one.
		List<AcceptedDuplicatePairEntity> toRemove = await context.AcceptedDuplicatePairs
			.Where(p => idSet.Contains(p.ReceiptIdA) && idSet.Contains(p.ReceiptIdB))
			.ToListAsync(cancellationToken);

		if (toRemove.Count == 0)
		{
			return 0;
		}

		// RemoveRange is converted to a soft delete by ApplicationDbContext.HandleSoftDelete.
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
			// Members whose receipt is soft-deleted or purged are dropped from the DISPLAY list: there
			// is nothing to show and nothing left to warn about. A component with fewer than two
			// surviving receipts can never produce a duplicate warning, so it is not listed either.
			// Restoring the receipt brings both the group and its (still-stored) acceptance back.
			List<DuplicateReceiptSummary> receipts = [.. members
				.Where(summaries.ContainsKey)
				.Select(id => summaries[id])
				.OrderBy(r => r.Date)
				.ThenBy(r => r.Location, StringComparer.Ordinal)];

			if (receipts.Count < 2)
			{
				continue;
			}

			// The COMPLETE member set goes out alongside it. Undo submits this, so every pair in the
			// component is removed even when some members no longer render — that is what stops the
			// pairs touching a deleted member being stranded with no way to reach them.
			groups.Add(new AcceptedDuplicateGroup(receipts, [.. members], componentAcceptedAt[root]));
		}

		groups = [.. groups.OrderByDescending(g => g.AcceptedAt)];
		return new AcceptedDuplicatesResult(groups, groups.Count);
	}

	private static async Task<HashSet<(Guid A, Guid B)>> LoadAcceptedPairsAsync(
		ApplicationDbContext context,
		HashSet<Guid> receiptIds,
		CancellationToken cancellationToken)
	{
		var rows = await context.AcceptedDuplicatePairs
			.AsNoTracking()
			.Where(p => receiptIds.Contains(p.ReceiptIdA) && receiptIds.Contains(p.ReceiptIdB))
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

		// LEFT JOIN ReceiptItems -> NormalizedDescriptions (via nullable FK), then GROUP BY the
		// coalesced canonical name. Aggregation happens in SQL (RECEIPTS-841) — the previous
		// implementation pulled every matching receipt item into memory before grouping, which
		// cannot support server-side pagination.
		var baseQuery = from ri in context.ReceiptItems.AsNoTracking().Where(ri => ri.DeletedAt == null)
						join r in receiptsQuery on ri.ReceiptId equals r.Id
						join n in context.NormalizedDescriptions.AsNoTracking() on ri.NormalizedDescriptionId equals n.Id into gj
						from n in gj.DefaultIfEmpty()
						group new { ri.TotalAmount, r.Date } by n.CanonicalName ?? NotNormalizedLabel into g
						select new
						{
							CanonicalName = g.Key,
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
		sortedQuery = sortedQuery.ThenBy(x => x.CanonicalName);

		// Skip/Take BEFORE materializing — the database paginates, only one page crosses the wire.
		var pagedGroups = await sortedQuery
			.Skip((page - 1) * pageSize)
			.Take(pageSize)
			.ToListAsync(cancellationToken);

		if (pagedGroups.Count == 0)
		{
			return new SpendingByNormalizedDescriptionResult([], totalCount, grandTotal, from, to);
		}

		Dictionary<string, string> dominantCurrencies = await GetDominantCurrenciesAsync(
			context,
			receiptsQuery,
			[.. pagedGroups.Select(x => x.CanonicalName)],
			cancellationToken);

		List<SpendingByNormalizedDescriptionItem> items = [.. pagedGroups.Select(x =>
			new SpendingByNormalizedDescriptionItem(
				x.CanonicalName,
				x.Total,
				dominantCurrencies.TryGetValue(x.CanonicalName, out string? currency) ? currency : Currency.USD.ToString(),
				x.ItemCount,
				ToDateTimeOffset(x.FirstSeen),
				ToDateTimeOffset(x.LastSeen)))];

		return new SpendingByNormalizedDescriptionResult(items, totalCount, grandTotal, from, to);
	}

	/// <summary>
	/// Resolves the dominant (most frequent) currency for each supplied bucket. Kept as a second,
	/// page-scoped query because the mode of a column is not expressible in the same grouped SQL
	/// statement — at most <c>pageSize</c> buckets are ever requested. Ties break on the
	/// <see cref="Currency"/> enum ordering, matching the pre-pagination behaviour.
	/// </summary>
	private static async Task<Dictionary<string, string>> GetDominantCurrenciesAsync(
		ApplicationDbContext context,
		IQueryable<ReceiptEntity> receiptsQuery,
		List<string> canonicalNames,
		CancellationToken cancellationToken)
	{
		var currencyCounts = await (
			from ri in context.ReceiptItems.AsNoTracking().Where(ri => ri.DeletedAt == null)
			join r in receiptsQuery on ri.ReceiptId equals r.Id
			join n in context.NormalizedDescriptions.AsNoTracking() on ri.NormalizedDescriptionId equals n.Id into gj
			from n in gj.DefaultIfEmpty()
			where canonicalNames.Contains(n.CanonicalName ?? NotNormalizedLabel)
			group ri by new { CanonicalName = n.CanonicalName ?? NotNormalizedLabel, ri.TotalAmountCurrency } into cg
			select new
			{
				cg.Key.CanonicalName,
				cg.Key.TotalAmountCurrency,
				Count = cg.Count(),
			}).ToListAsync(cancellationToken);

		return currencyCounts
			.GroupBy(x => x.CanonicalName)
			.ToDictionary(
				g => g.Key,
				g => g.OrderByDescending(x => x.Count)
					.ThenBy(x => x.TotalAmountCurrency)
					.Select(x => x.TotalAmountCurrency.ToString())
					.First());
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
