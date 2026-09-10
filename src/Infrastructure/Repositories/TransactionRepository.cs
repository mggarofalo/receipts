using System.Linq.Expressions;
using Application.Models;
using Infrastructure.Entities.Core;
using Infrastructure.Extensions;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class TransactionRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : ITransactionRepository
{
	private static readonly Dictionary<string, Expression<Func<TransactionEntity, object>>> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
	{
		["amount"] = e => e.Amount,
		["date"] = e => e.Date,
	};

	public async Task<TransactionEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Transactions
			.Include(transaction => transaction.Card)
			.FirstOrDefaultAsync(transaction => transaction.Id == id, cancellationToken);
	}

	public async Task<List<TransactionEntity>> GetByReceiptIdAsync(Guid receiptId, int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Transactions
			.IgnoreAutoIncludes()
			.Where(t => t.ReceiptId == receiptId)
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Date, e => e.Id, defaultDescending: true)
			.Skip(offset)
			.Take(limit)
			.Select(t => new TransactionEntity
			{
				Id = t.Id,
				ReceiptId = t.ReceiptId,
				Card = new CardEntity { Id = t.CardId, AccountId = t.Card!.AccountId },
				CardId = t.CardId,
				Amount = t.Amount,
				AmountCurrency = t.AmountCurrency,
				Date = t.Date
			})
			.ToListAsync(cancellationToken);
	}

	public async Task<List<TransactionEntity>> GetWithAccountByReceiptIdAsync(Guid receiptId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Transactions
			.IgnoreAutoIncludes()
			.Include(t => t.Card)
			.ThenInclude(card => card!.ParentAccount)
			.Where(t => t.ReceiptId == receiptId)
			.AsNoTracking()
			.OrderBy(e => e.Id)
			.ToListAsync(cancellationToken);
	}

	public async Task<int> GetByReceiptIdCountAsync(Guid receiptId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Transactions
			.Where(t => t.ReceiptId == receiptId)
			.CountAsync(cancellationToken);
	}

	public async Task<List<TransactionEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Transactions
			.IgnoreAutoIncludes()
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Date, e => e.Id, defaultDescending: true)
			.Skip(offset)
			.Take(limit)
			.Select(t => new TransactionEntity
			{
				Id = t.Id,
				ReceiptId = t.ReceiptId,
				Card = new CardEntity { Id = t.CardId, AccountId = t.Card!.AccountId },
				CardId = t.CardId,
				Amount = t.Amount,
				AmountCurrency = t.AmountCurrency,
				Date = t.Date
			})
			.ToListAsync(cancellationToken);
	}

	public async Task<List<TransactionEntity>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Transactions
			.OnlyDeleted()
			.Where(t => t.CascadeDeletedByParentId == null)
			.IgnoreAutoIncludes()
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Date, e => e.Id, defaultDescending: true)
			.Select(t => new TransactionEntity
			{
				Id = t.Id,
				ReceiptId = t.ReceiptId,
				Card = new CardEntity { Id = t.CardId, AccountId = t.Card!.AccountId },
				CardId = t.CardId,
				Amount = t.Amount,
				AmountCurrency = t.AmountCurrency,
				Date = t.Date,
				DeletedAt = t.DeletedAt
			})
			.Skip(offset)
			.Take(limit)
			.ToListAsync(cancellationToken);
	}

	public async Task<int> GetDeletedCountAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Transactions
			.OnlyDeleted()
			.Where(t => t.CascadeDeletedByParentId == null)
			.CountAsync(cancellationToken);
	}

	public async Task<List<TransactionEntity>> CreateAsync(List<TransactionEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		context.Transactions.AddRange(entities);
		// Load required response state before committing, so a failed read cannot leave
		// a saved transaction behind a failed create response. Tracking fixes up the cards.
		List<Guid> cardIds = entities.Select(entity => entity.CardId).Distinct().ToList();
		await context.Cards.IgnoreAutoIncludes().Where(card => cardIds.Contains(card.Id)).LoadAsync(cancellationToken);
		await context.SaveChangesAsync(cancellationToken);

		return entities;
	}

	public async Task UpdateAsync(List<TransactionEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IEnumerable<Guid> ids = entities.Select(e => e.Id);
		List<TransactionEntity> existingEntities = await context.Transactions
			.IgnoreAutoIncludes()
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		foreach (TransactionEntity entity in entities)
		{
			TransactionEntity existingEntity = existingEntities.Single(e => e.Id == entity.Id);
			context.Entry(existingEntity).CurrentValues.SetValues(entity);
		}

		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task<CascadeMutationResult> DeleteAsync(List<Guid> ids, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		List<TransactionEntity> entities = await context.Transactions
			.IgnoreAutoIncludes()
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);
		List<Guid> entityIds = [.. entities.Select(entity => entity.Id)];

		// Load owned YnabSyncRecords into the change tracker so the cascade soft-delete
		// fires (they carry a LocalTransactionId FK to Transactions). Without this a
		// synced transaction's active sync record lingers after the transaction is
		// soft-deleted and later blocks Empty Trash on the NO ACTION FK. See RECEIPTS-755.
		List<YnabSyncRecordEntity> syncRecords = await context.YnabSyncRecords
			.IgnoreAutoIncludes()
			.Where(s => entityIds.Contains(s.LocalTransactionId))
			.ToListAsync(cancellationToken);

		context.Transactions.RemoveRange(entities);
		await context.SaveChangesAsync(cancellationToken);
		return new CascadeMutationResult(entities.Count > 0, syncRecords.Count);
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Transactions.AnyAsync(e => e.Id == id, cancellationToken);
	}

	public async Task<int> GetCountAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Transactions.CountAsync(cancellationToken);
	}

	public async Task<CascadeMutationResult> RestoreAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		TransactionEntity? entity = await context.Transactions
			.IncludeDeleted()
			.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt != null, cancellationToken);

		if (entity is null)
		{
			return new CascadeMutationResult(false, 0);
		}

		entity.DeletedAt = null;
		entity.DeletedByUserId = null;
		entity.DeletedByApiKeyId = null;
		entity.CascadeDeletedByParentId = null;

		// Symmetric with DeleteAsync: revive the YnabSyncRecords this transaction
		// cascade-soft-deleted (tagged CascadeDeletedByParentId == transaction id), so a
		// delete -> restore round-trip does not leave a live transaction with dead sync
		// history. Only cascade-deleted children are restored; independently soft-deleted
		// sync records stay deleted. See RECEIPTS-755.
		await context.RestoreOwnedChildrenAsync<TransactionEntity>(id, cancellationToken);
		int syncRecordsChanged = CountRestoredSyncRecords(context);

		await context.SaveChangesAsync(cancellationToken);
		return new CascadeMutationResult(true, syncRecordsChanged);
	}

	private static int CountRestoredSyncRecords(ApplicationDbContext context)
		=> context.ChangeTracker.Entries<YnabSyncRecordEntity>().Count(entry =>
			entry.Property(syncRecord => syncRecord.DeletedAt).OriginalValue is not null
			&& entry.Property(syncRecord => syncRecord.DeletedAt).CurrentValue is null);
}
