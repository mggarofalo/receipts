using Application.Interfaces.Services;
using Application.Models.Dashboard;
using Application.Models.Reports;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class DashboardService(IDbContextFactory<ApplicationDbContext> contextFactory) : IDashboardService
{
	public async Task<DashboardSummaryResult> GetSummaryAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		IQueryable<ReceiptEntity> receiptsInRange = context.Receipts
			.AsNoTracking()
			.Where(r => r.Date >= startDate && r.Date <= endDate);

		int totalReceipts = await receiptsInRange.CountAsync(cancellationToken);

		if (totalReceipts == 0)
		{
			return new DashboardSummaryResult(0, 0, 0, new NameCountResult(null, 0), new NameCountResult(null, 0));
		}

		// Get receipt IDs in range for joining with transactions/items
		IQueryable<Guid> receiptIds = receiptsInRange.Select(r => r.Id);

		// Total spent = sum of all transaction amounts for receipts in range
		decimal totalSpent = await context.Transactions
			.AsNoTracking()
			.Where(t => receiptIds.Contains(t.ReceiptId))
			.SumAsync(t => t.Amount, cancellationToken);

		decimal averageTripAmount = totalReceipts > 0 ? Math.Round(totalSpent / totalReceipts, 2, MidpointRounding.AwayFromZero) : 0;

		// Most-used account = account with the most transactions in range
		// Single query: EF Core translates Account!.Name to a SQL JOIN, eliminating a second round-trip.
		var mostUsedAccountData = await context.Transactions
			.AsNoTracking()
			.Where(t => receiptIds.Contains(t.ReceiptId))
			.GroupBy(t => t.Card!.ParentAccount!.Name)
			.Select(g => new { AccountName = g.Key, Count = g.Count() })
			.OrderByDescending(g => g.Count)
			.FirstOrDefaultAsync(cancellationToken);

		NameCountResult mostUsedAccount = mostUsedAccountData != null
			? new NameCountResult(mostUsedAccountData.AccountName, mostUsedAccountData.Count)
			: new NameCountResult(null, 0);

		// Most-used category = the top-level item type with the most purchases in range, in today's tree
		CategoryReportResult roots = await CategoryReportReader.ReadAsync(context, context.ReceiptItems,
			startDate, endDate, CategoryReportView.CurrentHierarchy, CategoryReportRollup.Root,
			cancellationToken: cancellationToken);
		NameCountResult mostUsedCategory = roots.Buckets
			.Where(bucket => bucket.State == CategoryReportState.Classified && bucket.Path.Count > 0)
			.GroupBy(bucket => bucket.CategoryId)
			.Select(group => new NameCountResult(group.First().Path[0].Name, group.Sum(bucket => bucket.PurchaseCount)))
			.OrderByDescending(result => result.Count).ThenBy(result => result.Name, StringComparer.Ordinal)
			.FirstOrDefault() ?? new NameCountResult(null, 0);

		return new DashboardSummaryResult(totalReceipts, totalSpent, averageTripAmount, mostUsedAccount, mostUsedCategory);
	}

	public async Task<SpendingOverTimeResult> GetSpendingOverTimeAsync(DateOnly startDate, DateOnly endDate, string granularity, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		IQueryable<Guid> receiptIds = context.Receipts
			.AsNoTracking()
			.Where(r => r.Date >= startDate && r.Date <= endDate)
			.Select(r => r.Id);

		// Get all transactions for receipts in range, joined with receipt date
		var transactionsWithDate = context.Transactions
			.AsNoTracking()
			.Where(t => receiptIds.Contains(t.ReceiptId))
			.Join(
				context.Receipts.AsNoTracking(),
				t => t.ReceiptId,
				r => r.Id,
				(t, r) => new { t.Amount, r.Date });

		List<SpendingBucketResult> buckets;

		switch (granularity.ToLowerInvariant())
		{
			case "daily":
				var dailyMaterialized = await transactionsWithDate.ToListAsync(cancellationToken);
				buckets = dailyMaterialized
					.GroupBy(x => x.Date)
					.OrderBy(g => g.Key)
					.Select(g => new SpendingBucketResult(g.Key.ToString("yyyy-MM-dd"), g.Sum(x => x.Amount)))
					.ToList();
				break;

			case "quarterly":
				var quarterlyRaw = await transactionsWithDate
					.GroupBy(x => new { x.Date.Year, Quarter = (x.Date.Month - 1) / 3 + 1 })
					.Select(g => new { g.Key.Year, g.Key.Quarter, Amount = g.Sum(x => x.Amount) })
					.OrderBy(g => g.Year)
					.ThenBy(g => g.Quarter)
					.ToListAsync(cancellationToken);
				buckets = quarterlyRaw
					.Select(q => new SpendingBucketResult($"{q.Year} Q{q.Quarter}", q.Amount))
					.ToList();
				break;

			case "yearly":
				var yearlyRaw = await transactionsWithDate
					.GroupBy(x => x.Date.Year)
					.Select(g => new { Year = g.Key, Amount = g.Sum(x => x.Amount) })
					.OrderBy(g => g.Year)
					.ToListAsync(cancellationToken);
				buckets = yearlyRaw
					.Select(y => new SpendingBucketResult(y.Year.ToString(), y.Amount))
					.ToList();
				break;

			case "monthly":
			default:
				var monthlyRaw = await transactionsWithDate
					.GroupBy(x => new { x.Date.Year, x.Date.Month })
					.Select(g => new { g.Key.Year, g.Key.Month, Amount = g.Sum(x => x.Amount) })
					.OrderBy(g => g.Year)
					.ThenBy(g => g.Month)
					.ToListAsync(cancellationToken);
				buckets = monthlyRaw
					.Select(m => new SpendingBucketResult($"{m.Year}-{m.Month:D2}", m.Amount))
					.ToList();
				break;
		}

		return new SpendingOverTimeResult(buckets);
	}

	public Task<SpendingByCategoryResult> GetSpendingByCategoryAsync(DateOnly startDate, DateOnly endDate, int limit, CancellationToken cancellationToken)
		=> GetSpendingByCategoryAsync(startDate, endDate, limit, cancellationToken, new CategoryReportOptions());

	public async Task<SpendingByCategoryResult> GetSpendingByCategoryAsync(DateOnly startDate, DateOnly endDate, int limit, CancellationToken cancellationToken, CategoryReportOptions options)
	{
		if (limit is < 1 or > 100) { throw new ArgumentOutOfRangeException(nameof(limit)); }
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		if (options.CapturedCurrentRevisionId is Guid revision
			&& (options.View != CategoryReportView.CurrentHierarchy || revision == Guid.Empty
				|| !await context.TaxonomyRevisions.AsNoTracking().AnyAsync(row => row.Id == revision, cancellationToken)))
		{
			throw new ArgumentException("capturedCurrentRevisionId must identify an existing revision and requires currentHierarchy", "capturedCurrentRevisionId");
		}
		CategoryReportResult snapshot = await CategoryReportReader.ReadAsync(context, context.ReceiptItems,
			startDate, endDate, options.View, options.Rollup, options.CategoryId, cancellationToken,
			options.KnowledgeCutoff, options.CapturedCurrentRevisionId);
		Dictionary<string, decimal> totals = snapshot.Buckets.GroupBy(bucket => bucket.Currency)
			.ToDictionary(group => group.Key, group => group.Sum(bucket => bucket.Amount));
		List<SpendingCategoryItemResult> items = snapshot.Buckets.OrderByDescending(bucket => bucket.Amount)
			.ThenBy(bucket => bucket.PathRevisionId).ThenBy(bucket => bucket.CategoryId)
			.ThenBy(bucket => bucket.State).ThenBy(bucket => bucket.Currency, StringComparer.Ordinal).Take(limit)
			.Select(bucket => new SpendingCategoryItemResult(
				bucket.Path.Count > 0 ? bucket.Path[^1].Name : bucket.State.ToString(), bucket.Amount,
				totals[bucket.Currency] > 0 ? Math.Round(bucket.Amount / totals[bucket.Currency] * 100, 2, MidpointRounding.AwayFromZero) : 0)
			{ Bucket = bucket }).ToList();
		return new SpendingByCategoryResult(items) { Snapshot = snapshot };
	}

	public async Task<SpendingByAccountResult> GetSpendingByAccountAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		IQueryable<Guid> receiptIds = context.Receipts
			.AsNoTracking()
			.Where(r => r.Date >= startDate && r.Date <= endDate)
			.Select(r => r.Id);

		var accountSpending = await context.Transactions
			.AsNoTracking()
			.Where(t => receiptIds.Contains(t.ReceiptId))
			.GroupBy(t => t.Card!.AccountId)
			.Select(g => new { AccountId = g.Key, Amount = g.Sum(t => t.Amount) })
			.OrderByDescending(g => g.Amount)
			.ToListAsync(cancellationToken);

		decimal total = accountSpending.Sum(a => a.Amount);

		// Resolve the names of the cards' current parent accounts in one query.
		List<Guid> accountIds = accountSpending.Select(a => a.AccountId).ToList();
		Dictionary<Guid, string> accountNames = await context.Accounts
			.AsNoTracking()
			.Where(a => accountIds.Contains(a.Id))
			.ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);

		List<SpendingAccountItemResult> items = accountSpending
			.Select(a => new SpendingAccountItemResult(
				a.AccountId,
				accountNames.GetValueOrDefault(a.AccountId, "Unknown"),
				a.Amount,
				total > 0 ? Math.Round(a.Amount / total * 100, 2, MidpointRounding.AwayFromZero) : 0))
			.ToList();

		return new SpendingByAccountResult(items);
	}

	public async Task<SpendingByStoreResult> GetSpendingByStoreAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		IQueryable<ReceiptEntity> receiptsInRange = context.Receipts
			.AsNoTracking()
			.Where(r => r.Date >= startDate && r.Date <= endDate);

		// Get per-receipt totals (sum of transactions per receipt) with location
		var receiptTotals = await receiptsInRange
			.Select(r => new
			{
				r.Id,
				Location = r.Location ?? "Unknown",
				TransactionTotal = context.Transactions
					.AsNoTracking()
					.Where(t => t.ReceiptId == r.Id)
					.Sum(t => t.Amount)
			})
			.ToListAsync(cancellationToken);

		List<SpendingByStoreItemResult> items = receiptTotals
			.GroupBy(r => r.Location)
			.Select(g =>
			{
				int visitCount = g.Count();
				decimal totalAmount = g.Sum(r => r.TransactionTotal);
				decimal averagePerVisit = visitCount > 0 ? Math.Round(totalAmount / visitCount, 2, MidpointRounding.AwayFromZero) : 0;
				return new SpendingByStoreItemResult(g.Key, visitCount, totalAmount, averagePerVisit);
			})
			.OrderByDescending(i => i.TotalAmount)
			.ToList();

		return new SpendingByStoreResult(items);
	}

	public async Task<int> GetEarliestReceiptYearAsync(CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);

		DateOnly? earliestDate = await context.Receipts
			.AsNoTracking()
			.OrderBy(r => r.Date)
			.Select(r => (DateOnly?)r.Date)
			.FirstOrDefaultAsync(cancellationToken);

		return earliestDate?.Year ?? DateTime.Today.Year;
	}
}
