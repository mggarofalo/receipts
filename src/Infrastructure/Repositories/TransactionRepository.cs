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
		return await context.Transactions.FindAsync([id], cancellationToken);
	}

	public async Task<List<TransactionEntity>> GetByReceiptIdAsync(Guid receiptId, int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Transactions
			.IgnoreAutoIncludes()
			.Where(t => t.ReceiptId == receiptId)
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Date, defaultDescending: true)
			.Skip(offset)
			.Take(limit)
			.Select(t => new TransactionEntity
			{
				Id = t.Id,
				ReceiptId = t.ReceiptId,
				AccountId = t.AccountId,
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
			.Include(t => t.Account)
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
			.ApplySort(sort, AllowedSortColumns, e => e.Date, defaultDescending: true)
			.Skip(offset)
			.Take(limit)
			.Select(t => new TransactionEntity
			{
				Id = t.Id,
				ReceiptId = t.ReceiptId,
				AccountId = t.AccountId,
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
			.ApplySort(sort, AllowedSortColumns, e => e.Date, defaultDescending: true)
			.Select(t => new TransactionEntity
			{
				Id = t.Id,
				ReceiptId = t.ReceiptId,
				AccountId = t.AccountId,
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

	public async Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		List<TransactionEntity> entities = await context.Transactions
			.IgnoreAutoIncludes()
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		// Load owned YnabSyncRecords into the change tracker so the cascade soft-delete
		// fires (they carry a LocalTransactionId FK to Transactions). Without this a
		// synced transaction's active sync record lingers after the transaction is
		// soft-deleted and later blocks Empty Trash on the NO ACTION FK. See RECEIPTS-755.
		await context.YnabSyncRecords
			.IgnoreAutoIncludes()
			.Where(s => ids.Contains(s.LocalTransactionId))
			.LoadAsync(cancellationToken);

		context.Transactions.RemoveRange(entities);
		await context.SaveChangesAsync(cancellationToken);
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

	public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		TransactionEntity? entity = await context.Transactions
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
		await context.SaveChangesAsync(cancellationToken);
		return true;
	}
}
