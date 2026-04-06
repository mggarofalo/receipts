using Application.Interfaces.Services;
using Application.Models.Ynab;
using Common;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;

namespace Infrastructure.Services;

public class YnabSyncRecordService(IYnabSyncRecordRepository repository) : IYnabSyncRecordService
{
	public async Task<YnabSyncRecordDto> CreateAsync(Guid localTransactionId, string ynabBudgetId, YnabSyncType syncType, CancellationToken cancellationToken)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		YnabSyncRecordEntity entity = new()
		{
			LocalTransactionId = localTransactionId,
			YnabBudgetId = ynabBudgetId,
			SyncType = syncType,
			SyncStatus = YnabSyncStatus.Pending,
			CreatedAt = now,
			UpdatedAt = now,
		};

		YnabSyncRecordEntity created = await repository.CreateAsync(entity, cancellationToken);
		return ToDto(created);
	}

	public async Task<YnabSyncRecordDto?> GetByTransactionAndTypeAsync(Guid localTransactionId, YnabSyncType syncType, CancellationToken cancellationToken)
	{
		YnabSyncRecordEntity? entity = await repository.GetByTransactionAndTypeAsync(localTransactionId, syncType, cancellationToken);
		return entity is null ? null : ToDto(entity);
	}

	public async Task UpdateStatusAsync(Guid id, YnabSyncStatus status, string? ynabTransactionId, string? lastError, CancellationToken cancellationToken)
	{
		YnabSyncRecordEntity? entity = await repository.GetByIdAsync(id, cancellationToken);
		if (entity is null)
		{
			return;
		}

		entity.SyncStatus = status;
		entity.YnabTransactionId = ynabTransactionId ?? entity.YnabTransactionId;
		entity.LastError = lastError;
		entity.UpdatedAt = DateTimeOffset.UtcNow;

		if (status == YnabSyncStatus.Synced)
		{
			entity.SyncedAtUtc = DateTimeOffset.UtcNow;
		}

		await repository.UpdateAsync(entity, cancellationToken);
	}

	public async Task<List<ReceiptYnabSyncStatusDto>> GetSyncStatusesByReceiptIdsAsync(List<Guid> receiptIds, CancellationToken cancellationToken)
	{
		List<YnabSyncRecordEntity> syncRecords = await repository.GetByReceiptIdsAsync(receiptIds, cancellationToken);

		Dictionary<Guid, List<YnabSyncRecordEntity>> recordsByReceipt = [];
		foreach (YnabSyncRecordEntity record in syncRecords)
		{
			Guid receiptId = record.Transaction?.ReceiptId ?? Guid.Empty;
			if (receiptId == Guid.Empty)
			{
				continue;
			}

			if (!recordsByReceipt.TryGetValue(receiptId, out List<YnabSyncRecordEntity>? records))
			{
				records = [];
				recordsByReceipt[receiptId] = records;
			}
			records.Add(record);
		}

		List<ReceiptYnabSyncStatusDto> result = [];
		foreach (Guid receiptId in receiptIds)
		{
			if (!recordsByReceipt.TryGetValue(receiptId, out List<YnabSyncRecordEntity>? records) || records.Count == 0)
			{
				result.Add(new ReceiptYnabSyncStatusDto(receiptId, ReceiptSyncStatusValue.NotSynced));
				continue;
			}

			ReceiptSyncStatusValue aggregateStatus = AggregateStatus(records);
			result.Add(new ReceiptYnabSyncStatusDto(receiptId, aggregateStatus));
		}

		return result;
	}

	private static ReceiptSyncStatusValue AggregateStatus(List<YnabSyncRecordEntity> records)
	{
		bool hasFailed = false;
		bool hasPending = false;
		bool hasSynced = false;

		foreach (YnabSyncRecordEntity record in records)
		{
			switch (record.SyncStatus)
			{
				case YnabSyncStatus.Failed:
					hasFailed = true;
					break;
				case YnabSyncStatus.Pending:
					hasPending = true;
					break;
				case YnabSyncStatus.Synced:
					hasSynced = true;
					break;
			}
		}

		if (hasFailed)
		{
			return ReceiptSyncStatusValue.Failed;
		}

		if (hasPending)
		{
			return ReceiptSyncStatusValue.Pending;
		}

		if (hasSynced)
		{
			return ReceiptSyncStatusValue.Synced;
		}

		return ReceiptSyncStatusValue.NotSynced;
	}

	private static YnabSyncRecordDto ToDto(YnabSyncRecordEntity entity) => new(
		entity.Id,
		entity.LocalTransactionId,
		entity.YnabTransactionId,
		entity.YnabBudgetId,
		entity.YnabAccountId,
		entity.SyncType,
		entity.SyncStatus,
		entity.SyncedAtUtc,
		entity.LastError,
		entity.CreatedAt,
		entity.UpdatedAt);
}
