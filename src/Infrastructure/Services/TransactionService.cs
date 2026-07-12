using Application.Interfaces.Services;
using Application.Models;
using Domain.Aggregates;
using Domain.Core;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Services;

public class TransactionService(
	ITransactionRepository repository,
	TransactionMapper mapper,
	AccountMapper accountMapper,
	IDbContextFactory<ApplicationDbContext> contextFactory,
	ReceiptMapper receiptMapper,
	ReceiptItemMapper receiptItemMapper,
	AdjustmentMapper adjustmentMapper) : ITransactionService
{
	public async Task<List<Transaction>> CreateAsync(List<Transaction> models, Guid receiptId, CancellationToken cancellationToken)
	{
		List<TransactionEntity> transactionEntities = [.. models.Select(mapper.ToEntity)];

		foreach (TransactionEntity entity in transactionEntities)
		{
			entity.ReceiptId = receiptId;
		}

		List<TransactionEntity> createdTransactionEntities = await repository.CreateAsync(transactionEntities, cancellationToken);
		return [.. createdTransactionEntities.Select(mapper.ToDomain)];
	}


	public async Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken)
	{
		await repository.DeleteAsync(ids, cancellationToken);
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		return await repository.ExistsAsync(id, cancellationToken);
	}

	public async Task<PagedResult<Transaction>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		int total = await repository.GetCountAsync(cancellationToken);
		List<TransactionEntity> entities = await repository.GetAllAsync(offset, limit, sort, cancellationToken);
		List<Transaction> data = [.. entities.Select(mapper.ToDomain)];
		return new PagedResult<Transaction>(data, total, offset, limit);
	}

	public async Task<PagedResult<Transaction>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		int total = await repository.GetDeletedCountAsync(cancellationToken);
		List<TransactionEntity> entities = await repository.GetDeletedAsync(offset, limit, sort, cancellationToken);
		List<Transaction> data = [.. entities.Select(mapper.ToDomain)];
		return new PagedResult<Transaction>(data, total, offset, limit);
	}

	public async Task<Transaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		TransactionEntity? transactionEntity = await repository.GetByIdAsync(id, cancellationToken);
		return transactionEntity == null ? null : mapper.ToDomain(transactionEntity);
	}

	public async Task<PagedResult<Transaction>> GetByReceiptIdAsync(Guid receiptId, int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		int total = await repository.GetByReceiptIdCountAsync(receiptId, cancellationToken);
		List<TransactionEntity> entities = await repository.GetByReceiptIdAsync(receiptId, offset, limit, sort, cancellationToken);
		List<Transaction> data = entities.Select(mapper.ToDomain).ToList();
		return new PagedResult<Transaction>(data, total, offset, limit);
	}

	public async Task<int> GetCountAsync(CancellationToken cancellationToken)
	{
		return await repository.GetCountAsync(cancellationToken);
	}

	public async Task UpdateAsync(List<Transaction> models, Guid receiptId, CancellationToken cancellationToken)
	{
		List<TransactionEntity> transactionEntities = [.. models.Select(mapper.ToEntity)];

		foreach (TransactionEntity entity in transactionEntities)
		{
			entity.ReceiptId = receiptId;
		}

		await repository.UpdateAsync(transactionEntities, cancellationToken);
	}

	public async Task<List<TransactionAccount>> GetTransactionAccountsByReceiptIdAsync(Guid receiptId, CancellationToken cancellationToken)
	{
		List<TransactionEntity> entities = await repository.GetWithAccountByReceiptIdAsync(receiptId, cancellationToken);
		return
		[
			.. entities
				.Where(e => e.Account != null)
				.Select(e => new TransactionAccount
				{
					Transaction = mapper.ToDomain(e),
					Account = accountMapper.ToDomain(e.Account!)
				})
		];
	}

	public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
	{
		return await repository.RestoreAsync(id, cancellationToken);
	}

	public async Task<List<Transaction>> CreateWithBalanceValidationAsync(
		List<Transaction> models,
		Guid receiptId,
		Action<ReceiptBalanceState> validate,
		CancellationToken cancellationToken)
	{
		return await ExecuteBalanceGuardedAsync(receiptId, validate, async (context, token) =>
		{
			List<TransactionEntity> entities = [.. models.Select(mapper.ToEntity)];
			foreach (TransactionEntity entity in entities)
			{
				entity.ReceiptId = receiptId;
			}

			context.Transactions.AddRange(entities);
			await context.SaveChangesAsync(token);
			return entities.Select(mapper.ToDomain).ToList();
		}, cancellationToken);
	}

	public async Task UpdateWithBalanceValidationAsync(
		List<Transaction> models,
		Guid receiptId,
		Action<ReceiptBalanceState> validate,
		CancellationToken cancellationToken)
	{
		await ExecuteBalanceGuardedAsync(receiptId, validate, async (context, token) =>
		{
			List<Guid> ids = [.. models.Select(m => m.Id)];
			List<TransactionEntity> existingEntities = await context.Transactions
				.IgnoreAutoIncludes()
				.Where(e => ids.Contains(e.Id))
				.ToListAsync(token);

			foreach (Transaction model in models)
			{
				TransactionEntity entity = mapper.ToEntity(model);
				entity.ReceiptId = receiptId;
				TransactionEntity existingEntity = existingEntities.Single(e => e.Id == entity.Id);
				context.Entry(existingEntity).CurrentValues.SetValues(entity);
			}

			await context.SaveChangesAsync(token);
			return true;
		}, cancellationToken);
	}

	// Serializes validate-and-write per receipt at the database level (RECEIPTS-764). One
	// DbContext and one explicit transaction: lock the receipt row FOR UPDATE, re-read the
	// receipt's children inside that lock, run the caller's balance validation against the
	// fresh snapshot, then apply the write and commit. Two concurrent calls for the same
	// receipt block on the row lock, so the second sees the first's committed write and can
	// reject an over-allocation. Providers without row locks / transactions (InMemory in unit
	// tests) degrade to a plain read-validate-write in a single context.
	//
	// RECEIPTS-805: this lock serializes the balance invariant ONLY against concurrent TRANSACTION
	// writes (this create/update path). Concurrent edits to a receipt's items, adjustments, or tax
	// take no receipt-level lock, so a transaction write racing such an edit can still momentarily
	// leave the receipt out of balance. Extending the FOR UPDATE guard to cover child/adjustment/tax
	// edits is deferred — that wider scope is a known, accepted limitation, not an oversight here.
	private async Task<T> ExecuteBalanceGuardedAsync<T>(
		Guid receiptId,
		Action<ReceiptBalanceState> validate,
		Func<ApplicationDbContext, CancellationToken, Task<T>> write,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();

		bool useTransaction = context.Database.IsRelational();
		bool useRowLock = context.Database.IsNpgsql();

		IDbContextTransaction? transaction = useTransaction
			? await context.Database.BeginTransactionAsync(cancellationToken)
			: null;

		try
		{
			ReceiptEntity receipt = await LockAndLoadReceiptAsync(context, receiptId, useRowLock, cancellationToken)
				?? throw new KeyNotFoundException($"Receipt {receiptId} not found.");

			// Re-read children INSIDE the lock so a waiter observes the lock holder's committed
			// state. IgnoreAutoIncludes keeps these to the amount columns the balance equation
			// needs (no Account / Card / Receipt navigations).
			List<ReceiptItemEntity> items = await context.ReceiptItems
				.IgnoreAutoIncludes().AsNoTracking()
				.Where(i => i.ReceiptId == receiptId)
				.ToListAsync(cancellationToken);

			List<AdjustmentEntity> adjustments = await context.Adjustments
				.IgnoreAutoIncludes().AsNoTracking()
				.Where(a => a.ReceiptId == receiptId)
				.ToListAsync(cancellationToken);

			List<TransactionEntity> existingTransactions = await context.Transactions
				.IgnoreAutoIncludes().AsNoTracking()
				.Where(t => t.ReceiptId == receiptId)
				.ToListAsync(cancellationToken);

			ReceiptBalanceState state = new()
			{
				Receipt = receiptMapper.ToDomain(receipt),
				Items = [.. items.Select(receiptItemMapper.ToDomain)],
				Adjustments = [.. adjustments.Select(adjustmentMapper.ToDomain)],
				ExistingTransactions = [.. existingTransactions.Select(mapper.ToDomain)],
			};

			validate(state);

			T result = await write(context, cancellationToken);

			if (transaction is not null)
			{
				await transaction.CommitAsync(cancellationToken);
			}

			return result;
		}
		finally
		{
			if (transaction is not null)
			{
				await transaction.DisposeAsync();
			}
		}
	}

	private static async Task<ReceiptEntity?> LockAndLoadReceiptAsync(
		ApplicationDbContext context,
		Guid receiptId,
		bool useRowLock,
		CancellationToken cancellationToken)
	{
		if (useRowLock)
		{
			// Take a row-level write lock on the non-soft-deleted receipt. Executed as a
			// top-level statement (ToListAsync with no further composition) because PostgreSQL
			// rejects FOR UPDATE inside the subquery EF would emit for a composed FirstOrDefault.
			// An empty result means the receipt is missing or soft-deleted (RECEIPTS-763).
			List<Guid> locked = await context.Database
				.SqlQueryRaw<Guid>(
					"""SELECT "Id" AS "Value" FROM "receipts"."Receipts" WHERE "Id" = {0} AND "DeletedAt" IS NULL FOR UPDATE""",
					receiptId)
				.ToListAsync(cancellationToken);

			if (locked.Count == 0)
			{
				return null;
			}
		}

		// The global query filter excludes soft-deleted receipts, so this enforces the
		// existence / soft-delete guard even on providers without row locks (InMemory).
		return await context.Receipts
			.AsNoTracking()
			.FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);
	}
}

