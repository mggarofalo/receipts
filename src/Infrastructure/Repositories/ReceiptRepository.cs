using System.Linq.Expressions;
using Application.Models;
using Infrastructure.Entities.Core;
using Infrastructure.Extensions;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class ReceiptRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : IReceiptRepository
{
	private static readonly Dictionary<string, Expression<Func<ReceiptEntity, object>>> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
	{
		["location"] = e => e.Location,
		["date"] = e => e.Date,
		["taxAmount"] = e => e.TaxAmount,
	};

	public async Task<ReceiptEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Receipts.FindAsync([id], cancellationToken);
	}

	public Task<List<ReceiptEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
		=> GetAllAsync(offset, limit, sort, accountId: null, cardId: null, q: null, location: null, cancellationToken);

	public async Task<List<ReceiptEntity>> GetAllAsync(int offset, int limit, SortParams sort, Guid? accountId, Guid? cardId, string? q, string? location, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<ReceiptEntity> query = ApplyTransactionFilters(context, context.Receipts.AsNoTracking(), accountId, cardId);
		query = ApplySearchFilter(query, q);
		query = ApplyLocationFilter(query, location);
		return await query
			.ApplySort(sort, AllowedSortColumns, e => e.Date, e => e.Id, defaultDescending: true)
			.Skip(offset)
			.Take(limit)
			.Select(r => new ReceiptEntity
			{
				Id = r.Id,
				Location = r.Location,
				Date = r.Date,
				TaxAmount = r.TaxAmount,
				TaxAmountCurrency = r.TaxAmountCurrency
			})
			.ToListAsync(cancellationToken);
	}

	private static IQueryable<ReceiptEntity> ApplySearchFilter(IQueryable<ReceiptEntity> query, string? q)
	{
		if (string.IsNullOrWhiteSpace(q))
		{
			return query;
		}

		string pattern = "%" + EscapeLikePattern(q.Trim()) + "%";
		return query.Where(r => EF.Functions.ILike(r.Location, pattern, LikeEscapeCharacter));
	}

	// Literal equality on Location, as opposed to ApplySearchFilter's case-insensitive substring
	// match. Drill-downs from the Spending by Location report (RECEIPTS-841) must return exactly the
	// rows the aggregate counted, and that report groups on the raw Location column
	// (`group ... by (r.Location ?? "")`), which Postgres compares byte-for-byte. So this filter has
	// to be byte-for-byte too:
	//   - Not ILIKE/case-insensitive. "Walmart" and "walmart" are two separate report rows with
	//     separate visit counts; a case-insensitive filter would return the union of both and
	//     contradict the count the user just clicked on.
	//   - Not trimmed. "Target " (trailing space) is its own report bucket, so trimming the incoming
	//     value here would make that row's drill-down match nothing.
	// A plain equality also indexes better than ILIKE. Callers must therefore pass Location through
	// verbatim — see ReceiptsController.GetAllReceipts.
	private static IQueryable<ReceiptEntity> ApplyLocationFilter(IQueryable<ReceiptEntity> query, string? location)
	{
		if (string.IsNullOrEmpty(location))
		{
			return query;
		}

		return query.Where(r => r.Location == location);
	}

	// MUST be passed to every EF.Functions.ILike call alongside an escaped pattern. The two-argument
	// overload makes Npgsql emit `ESCAPE ''`, which disables backslash escaping outright — the
	// backslashes EscapeLikePattern inserts are then ignored and a literal '%' or '_' in user input
	// still behaves as a SQL wildcard. The three-argument overload emits `ESCAPE '\'` and makes the
	// escaping actually take effect.
	private const string LikeEscapeCharacter = "\\";

	private static string EscapeLikePattern(string value)
		=> value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

	// Filter receipts down to those with at least one transaction matching the supplied
	// accountId / cardId. Account filter matches transactions through Card.AccountId.
	// Transaction.AccountId is still carried additively (drop is a separate later phase).
	private static IQueryable<ReceiptEntity> ApplyTransactionFilters(ApplicationDbContext context, IQueryable<ReceiptEntity> query, Guid? accountId, Guid? cardId)
	{
		if (cardId.HasValue)
		{
			Guid id = cardId.Value;
			query = query.Where(r => context.Transactions.Any(t =>
				t.ReceiptId == r.Id && t.CardId == id));
		}

		if (accountId.HasValue)
		{
			Guid id = accountId.Value;
			query = query.Where(r => context.Transactions.Any(t =>
				t.ReceiptId == r.Id && t.Card != null && t.Card.AccountId == id));
		}

		return query;
	}

	public async Task<List<ReceiptEntity>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Receipts
			.OnlyDeleted()
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Date, e => e.Id, defaultDescending: true)
			.Select(r => new ReceiptEntity
			{
				Id = r.Id,
				Location = r.Location,
				Date = r.Date,
				TaxAmount = r.TaxAmount,
				TaxAmountCurrency = r.TaxAmountCurrency,
				DeletedAt = r.DeletedAt
			})
			.Skip(offset)
			.Take(limit)
			.ToListAsync(cancellationToken);
	}

	public async Task<int> GetDeletedCountAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Receipts
			.OnlyDeleted()
			.CountAsync(cancellationToken);
	}

	public async Task<List<ReceiptEntity>> CreateAsync(List<ReceiptEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		context.Receipts.AddRange(entities);
		await context.SaveChangesAsync(cancellationToken);
		return entities;
	}

	public async Task UpdateAsync(List<ReceiptEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IEnumerable<Guid> ids = entities.Select(e => e.Id);
		List<ReceiptEntity> existingEntities = await context.Receipts
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		foreach (ReceiptEntity entity in entities)
		{
			ReceiptEntity existingEntity = existingEntities.Single(e => e.Id == entity.Id);
			context.Entry(existingEntity).CurrentValues.SetValues(entity);
		}

		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task UpdateImagePathsAsync(Guid id, string originalImagePath, string processedImagePath, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		ReceiptEntity entity = await context.Receipts.FindAsync([id], cancellationToken)
			?? throw new KeyNotFoundException($"Receipt {id} not found.");

		entity.OriginalImagePath = originalImagePath;
		entity.ProcessedImagePath = processedImagePath;

		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		List<ReceiptEntity> entities = await context.Receipts
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		// Load owned children into the change tracker so cascade soft-delete fires
		await context.ReceiptItems.IgnoreAutoIncludes().Where(i => ids.Contains(i.ReceiptId)).LoadAsync(cancellationToken);
		await context.Transactions.IgnoreAutoIncludes().Where(t => ids.Contains(t.ReceiptId)).LoadAsync(cancellationToken);
		await context.Adjustments.IgnoreAutoIncludes().Where(a => ids.Contains(a.ReceiptId)).LoadAsync(cancellationToken);

		// Also load the YnabSyncRecords owned by those transactions so the two-level
		// cascade (Receipt -> Transaction -> YnabSyncRecord) soft-deletes them in
		// FK-correct order. Otherwise a synced transaction's active sync record lingers
		// and later blocks Empty Trash on the NO ACTION FK. See RECEIPTS-755.
		await context.YnabSyncRecords
			.IgnoreAutoIncludes()
			.Where(s => context.Transactions
				.Where(t => ids.Contains(t.ReceiptId))
				.Select(t => t.Id)
				.Contains(s.LocalTransactionId))
			.LoadAsync(cancellationToken);

		context.Receipts.RemoveRange(entities);
		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Receipts.AnyAsync(e => e.Id == id, cancellationToken);
	}

	public Task<int> GetCountAsync(CancellationToken cancellationToken)
		=> GetCountAsync(accountId: null, cardId: null, q: null, location: null, cancellationToken);

	public async Task<int> GetCountAsync(Guid? accountId, Guid? cardId, string? q, string? location, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<ReceiptEntity> query = ApplyTransactionFilters(context, context.Receipts.AsNoTracking(), accountId, cardId);
		query = ApplySearchFilter(query, q);
		query = ApplyLocationFilter(query, location);
		return await query.CountAsync(cancellationToken);
	}

	public async Task<List<string>> GetDistinctLocationsAsync(string? query, int limit, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();

		IQueryable<ReceiptEntity> receipts = context.Receipts.AsNoTracking();

		if (!string.IsNullOrWhiteSpace(query))
		{
			string pattern = EscapeLikePattern(query) + "%";
			receipts = receipts.Where(r => EF.Functions.ILike(r.Location, pattern, LikeEscapeCharacter));
		}

		List<string> locations = await receipts
			.GroupBy(r => r.Location)
			.OrderByDescending(g => g.Count())
			.ThenBy(g => g.Key)
			.Select(g => g.Key)
			.Take(limit)
			.ToListAsync(cancellationToken);

		return locations;
	}

	public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		ReceiptEntity? entity = await context.Receipts
			.IncludeDeleted()
			.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt != null, cancellationToken);

		if (entity is null)
		{
			return false;
		}

		entity.DeletedAt = null;
		entity.DeletedByUserId = null;
		entity.DeletedByApiKeyId = null;
		entity.CascadeDeletedByParentId = null;

		// Restores cascade-soft-deleted children recursively: the receipt's transactions
		// and, in turn, the YnabSyncRecords those transactions cascade-soft-deleted
		// (tagged with the transaction id). Mirrors the two-level cascade delete. RECEIPTS-755
		await context.RestoreOwnedChildrenAsync<ReceiptEntity>(id, cancellationToken);

		await context.SaveChangesAsync(cancellationToken);
		return true;
	}
}
