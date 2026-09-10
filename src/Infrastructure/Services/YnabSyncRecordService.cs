using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Models.Ynab;
using Common;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;

namespace Infrastructure.Services;

public class YnabSyncRecordService(
	IYnabSyncRecordRepository repository,
	ICommittedChangePublisher committedChangePublisher) : IYnabSyncRecordService
{
	public YnabSyncRecordService(IYnabSyncRecordRepository repository)
		: this(repository, new NullCommittedChangePublisher())
	{
	}

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
		await committedChangePublisher.PublishAsync(new CommittedEntityChange(
			CommittedEntityType.YnabSyncRecord,
			CommittedChangeType.Created,
			created.Id));
		return ToDto(created);
	}

	public async Task<YnabSyncRecordDto?> GetByTransactionTypeAndBudgetAsync(
		Guid localTransactionId,
		YnabSyncType syncType,
		string ynabBudgetId,
		CancellationToken cancellationToken)
	{
		YnabSyncRecordEntity? entity = await repository.GetByTransactionTypeAndBudgetAsync(
			localTransactionId,
			syncType,
			ynabBudgetId,
			cancellationToken);
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

		if (await repository.UpdateAsync(entity, cancellationToken))
		{
			await committedChangePublisher.PublishAsync(new CommittedEntityChange(
				CommittedEntityType.YnabSyncRecord,
				CommittedChangeType.Updated,
				id));
		}
	}

	public async Task<List<ReceiptYnabSyncStatusDto>> GetSyncStatusesByReceiptIdsAndBudgetAsync(
		List<Guid> receiptIds,
		string ynabBudgetId,
		CancellationToken cancellationToken)
	{
		List<YnabSyncRecordEntity> syncRecords = await repository.GetByReceiptIdsAndBudgetAsync(
			receiptIds,
			ynabBudgetId,
			cancellationToken);

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
		// Prioritize TransactionPush records — they represent the primary sync operation.
		// MemoUpdate is secondary; a failed memo update should not mask a successful push.
		List<YnabSyncRecordEntity> pushRecords = records.Where(r => r.SyncType == Common.YnabSyncType.TransactionPush).ToList();
		List<YnabSyncRecordEntity> primaryRecords = pushRecords.Count > 0 ? pushRecords : records;

		bool hasFailed = false;
		bool hasPending = false;
		bool hasSynced = false;

		foreach (YnabSyncRecordEntity record in primaryRecords)
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
	public Task<DateTimeOffset?> GetLatestSuccessfulSyncTimestampAsync(string ynabBudgetId, CancellationToken cancellationToken)
		=> repository.GetLatestSuccessfulSyncTimestampAsync(ynabBudgetId, cancellationToken);

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
