using Application.Duplicates;
using Application.Interfaces.Services;
using Application.Models.Reports;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class DuplicateReportReader(IDbContextFactory<ApplicationDbContext> contextFactory) : IDuplicateReportReader
{
	public async Task<DuplicateDetectionResult> GetDuplicatesAsync(
		string matchOn,
		string locationTolerance,
		decimal totalTolerance,
		bool includeAccepted,
		CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		List<DuplicateReceiptSummary> receipts = await (from r in context.Receipts.AsNoTracking()
														where r.DeletedAt == null
														let transactionTotal = context.Transactions
															.Where(t => t.ReceiptId == r.Id && t.DeletedAt == null)
															.Sum(t => (decimal?)t.Amount) ?? 0m
														select new DuplicateReceiptSummary(
															r.Id,
															r.Location,
															r.Date,
															transactionTotal
														)).ToListAsync(cancellationToken);

		List<DuplicateGroup> groups = DuplicateDetectionPolicy.Group(receipts, matchOn, locationTolerance, totalTolerance);

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
			bool isAccepted = DuplicateAcceptancePolicy.IsFullyAccepted(group.Receipts.Select(r => r.ReceiptId), acceptedPairs);
			if (isAccepted && !includeAccepted)
			{
				continue;
			}

			visibleGroups.Add(group with { IsAccepted = isAccepted });
		}

		int totalDuplicateReceipts = visibleGroups.Sum(g => g.Receipts.Count);
		return new DuplicateDetectionResult(visibleGroups, visibleGroups.Count, totalDuplicateReceipts);
	}

	public async Task<AcceptedDuplicatesResult> GetAcceptedDuplicatesAsync(CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		var pairRows = await context.AcceptedDuplicatePairs
			.AsNoTracking()
			.Select(p => new { p.ReceiptIdA, p.ReceiptIdB, p.AcceptedAt })
			.ToListAsync(cancellationToken);

		List<DuplicateAcceptedPair> pairs = [.. pairRows
			.Select(p => new DuplicateAcceptedPair(p.ReceiptIdA, p.ReceiptIdB, p.AcceptedAt))];

		if (pairs.Count == 0)
		{
			return new AcceptedDuplicatesResult([], 0);
		}

		List<DuplicateAcceptanceComponent> components = DuplicateAcceptancePolicy.Components(pairs);
		HashSet<Guid> allMembers = [.. components.SelectMany(c => c.ReceiptIds)];

		Dictionary<Guid, DuplicateReceiptSummary> summaries = await (
			from r in context.Receipts.AsNoTracking()
			where r.DeletedAt == null && allMembers.Contains(r.Id)
			let transactionTotal = context.Transactions
				.Where(t => t.ReceiptId == r.Id && t.DeletedAt == null)
				.Sum(t => (decimal?)t.Amount) ?? 0m
			select new DuplicateReceiptSummary(r.Id, r.Location, r.Date, transactionTotal))
			.ToDictionaryAsync(s => s.ReceiptId, cancellationToken);

		return DuplicateAcceptancePolicy.ProjectGroups(components, summaries);
	}

	internal static async Task<int> CountUnacceptedDefaultGroupsAsync(ApplicationDbContext context, CancellationToken cancellationToken)
	{
		// Project only group cardinalities; no financial totals or receipt objects are loaded for a badge.
		var groups = await context.Receipts.AsNoTracking()
			.GroupBy(r => new { r.Date, r.Location }).Where(g => g.Count() > 1)
			.Select(g => new { g.Key.Date, g.Key.Location, Count = g.Count() }).ToListAsync(cancellationToken);
		if (groups.Count == 0)
		{
			return 0;
		}

		var accepted = await (from pair in context.AcceptedDuplicatePairs.AsNoTracking()
							  join a in context.Receipts on pair.ReceiptIdA equals a.Id
							  join b in context.Receipts on pair.ReceiptIdB equals b.Id
							  where a.Date == b.Date && a.Location == b.Location
							  group pair by new { a.Date, a.Location } into g
							  select new { g.Key.Date, g.Key.Location, Count = g.LongCount() }).ToListAsync(cancellationToken);
		Dictionary<(DateOnly, string), long> acceptedCounts = accepted.ToDictionary(g => (g.Date, g.Location), g => g.Count);
		return groups.Count(g => !DuplicateAcceptancePolicy.IsFullyAccepted(g.Count, acceptedCounts.GetValueOrDefault((g.Date, g.Location))));
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

}
