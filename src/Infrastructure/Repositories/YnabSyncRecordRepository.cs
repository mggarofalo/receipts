using Common;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class YnabSyncRecordRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : IYnabSyncRecordRepository
{
	public async Task<YnabSyncRecordEntity> CreateAsync(YnabSyncRecordEntity entity, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		context.YnabSyncRecords.Add(entity);
		await context.SaveChangesAsync(cancellationToken);
		return entity;
	}

	public async Task<YnabSyncRecordEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabSyncRecords.FindAsync([id], cancellationToken);
	}

	public async Task<YnabSyncRecordEntity?> GetByTransactionTypeAndBudgetAsync(
		Guid localTransactionId,
		YnabSyncType syncType,
		string ynabBudgetId,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabSyncRecords
			.FirstOrDefaultAsync(
				e => e.LocalTransactionId == localTransactionId &&
					e.SyncType == syncType &&
					e.YnabBudgetId == ynabBudgetId,
				cancellationToken);
	}

	public async Task<PreparedYnabPushOperation> GetOrCreatePushOperationAsync(
		YnabSyncRecordEntity proposed,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabSyncRecordEntity? existing = await context.YnabSyncRecords
			.AsNoTracking()
			.FirstOrDefaultAsync(
				e => e.LocalTransactionId == proposed.LocalTransactionId &&
					e.SyncType == YnabSyncType.TransactionPush &&
					e.YnabBudgetId == proposed.YnabBudgetId,
				cancellationToken);
		if (existing is not null)
		{
			return new PreparedYnabPushOperation(existing, Created: false);
		}

		context.YnabSyncRecords.Add(proposed);
		try
		{
			await context.SaveChangesAsync(cancellationToken);
			return new PreparedYnabPushOperation(proposed, Created: true);
		}
		catch (DbUpdateException)
		{
			// A concurrent request may have inserted the unique operation first. Resolve
			// that race by returning the winner; preserve unrelated persistence failures.
			context.ChangeTracker.Clear();
			existing = await context.YnabSyncRecords
				.AsNoTracking()
				.FirstOrDefaultAsync(
					e => e.LocalTransactionId == proposed.LocalTransactionId &&
						e.SyncType == YnabSyncType.TransactionPush &&
						e.YnabBudgetId == proposed.YnabBudgetId,
					cancellationToken);
			if (existing is null)
			{
				throw;
			}

			return new PreparedYnabPushOperation(existing, Created: false);
		}
	}

	public async Task<YnabSyncRecordEntity?> TryClaimPushOperationAsync(
		Guid id,
		Guid claimToken,
		DateTimeOffset now,
		DateTimeOffset staleBefore,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		int claimed = await context.YnabSyncRecords
			.Where(e => e.Id == id &&
				e.SyncType == YnabSyncType.TransactionPush &&
				e.RequestPayloadJson != null &&
				e.ImportId != null &&
				e.PayloadHash != null &&
				e.SourceVersion != null &&
				e.SyncStatus != YnabSyncStatus.Synced &&
				(e.ClaimToken == null || e.ClaimedAtUtc < staleBefore))
			.ExecuteUpdateAsync(setters => setters
				.SetProperty(e => e.ClaimToken, claimToken)
				.SetProperty(e => e.ClaimedAtUtc, now)
				.SetProperty(e => e.LastAttemptAtUtc, now)
				.SetProperty(e => e.AttemptCount, e => e.AttemptCount + 1)
				.SetProperty(e => e.SyncStatus, YnabSyncStatus.Pending)
				.SetProperty(e => e.LastError, (string?)null)
				.SetProperty(e => e.UpdatedAt, now),
				cancellationToken);
		if (claimed == 0)
		{
			return null;
		}

		return await context.YnabSyncRecords
			.AsNoTracking()
			.SingleAsync(e => e.Id == id, cancellationToken);
	}

	public async Task<bool> CompletePushOperationAsync(
		Guid id,
		Guid claimToken,
		YnabSyncStatus status,
		string? ynabTransactionId,
		string? lastError,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabSyncRecords
			.Where(e => e.Id == id && e.ClaimToken == claimToken)
			.ExecuteUpdateAsync(setters => setters
				.SetProperty(e => e.SyncStatus, status)
				.SetProperty(e => e.YnabTransactionId, e => ynabTransactionId ?? e.YnabTransactionId)
				.SetProperty(e => e.LastError, lastError)
				.SetProperty(e => e.SyncedAtUtc, e => status == YnabSyncStatus.Synced ? now : e.SyncedAtUtc)
				.SetProperty(e => e.ClaimToken, (Guid?)null)
				.SetProperty(e => e.ClaimedAtUtc, (DateTimeOffset?)null)
				.SetProperty(e => e.UpdatedAt, now),
				cancellationToken) == 1;
	}

	public async Task<bool> UpdateAsync(YnabSyncRecordEntity entity, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabSyncRecordEntity? existing = await context.YnabSyncRecords.FindAsync([entity.Id], cancellationToken);
		if (existing is null)
		{
			return false;
		}

		context.Entry(existing).CurrentValues.SetValues(entity);
		return await context.SaveChangesAsync(cancellationToken) > 0;
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabSyncRecordEntity? entity = await context.YnabSyncRecords.FindAsync([id], cancellationToken);
		if (entity is not null)
		{
			context.YnabSyncRecords.Remove(entity);
			await context.SaveChangesAsync(cancellationToken);
		}
	}

	public async Task<List<YnabSyncRecordEntity>> GetByReceiptIdsAndBudgetAsync(
		List<Guid> receiptIds,
		string ynabBudgetId,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabSyncRecords
			.Where(sr => sr.YnabBudgetId == ynabBudgetId && context.Set<TransactionEntity>()
				.Where(t => receiptIds.Contains(t.ReceiptId))
				.Select(t => t.Id)
				.Contains(sr.LocalTransactionId))
			.Include(sr => sr.Transaction)
			.AsNoTracking()
			.ToListAsync(cancellationToken);
	}

	public async Task<DateTimeOffset?> GetLatestSuccessfulSyncTimestampAsync(string ynabBudgetId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.YnabSyncRecords
			.Where(e => e.YnabBudgetId == ynabBudgetId && e.SyncStatus == YnabSyncStatus.Synced && e.SyncedAtUtc != null)
			.OrderByDescending(e => e.SyncedAtUtc)
			.Select(e => e.SyncedAtUtc)
			.FirstOrDefaultAsync(cancellationToken);
	}
}
