using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Models.Ynab;
using Common;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;

namespace Infrastructure.Services;

public class YnabSyncRecordService(
	IYnabSyncRecordRepository repository,
	ICommittedChangePublisher committedChangePublisher,
	TimeProvider timeProvider) : IYnabSyncRecordService
{
	private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);
	private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

	public YnabSyncRecordService(
		IYnabSyncRecordRepository repository,
		ICommittedChangePublisher committedChangePublisher)
		: this(repository, committedChangePublisher, TimeProvider.System)
	{
	}

	public YnabSyncRecordService(IYnabSyncRecordRepository repository)
		: this(repository, new NullCommittedChangePublisher(), TimeProvider.System)
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

	public async Task<YnabPushOperation> PreparePushOperationAsync(
		Guid localTransactionId,
		string ynabBudgetId,
		YnabCreateTransactionRequest request,
		string sourceVersion,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(request.ImportId))
		{
			throw new ArgumentException("A transaction-push operation requires an import ID.", nameof(request));
		}

		string payloadJson = JsonSerializer.Serialize(request, PayloadJsonOptions);
		string payloadHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
		DateTimeOffset now = timeProvider.GetUtcNow();
		YnabSyncRecordEntity proposed = new()
		{
			LocalTransactionId = localTransactionId,
			YnabBudgetId = ynabBudgetId,
			YnabAccountId = request.AccountId,
			ImportId = request.ImportId,
			RequestPayloadJson = payloadJson,
			PayloadHash = payloadHash,
			SourceVersion = sourceVersion,
			SyncType = YnabSyncType.TransactionPush,
			SyncStatus = YnabSyncStatus.Pending,
			CreatedAt = now,
			UpdatedAt = now,
		};

		PreparedYnabPushOperation prepared = await repository.GetOrCreatePushOperationAsync(proposed, cancellationToken);
		if (prepared.Created)
		{
			await PublishChangeAsync(prepared.Record.Id, CommittedChangeType.Created);
		}

		YnabSyncRecordEntity record = prepared.Record;
		if (record.SyncStatus == YnabSyncStatus.Synced && record.RequestPayloadJson is null)
		{
			// Successful legacy rows are terminal and must remain so. They are skipped by
			// the push handler and never need a reconstructed request.
			return new YnabPushOperation(record.Id, record.SyncStatus, null, string.Empty, string.Empty,
				record.AttemptCount, record.YnabTransactionId, record.LastError);
		}

		if (record.RequestPayloadJson is null || record.ImportId is null || record.PayloadHash is null || record.SourceVersion is null)
		{
			const string legacyError = "This YNAB attempt predates immutable operation tracking and cannot be retried automatically. Review the destination transaction before resolving it.";
			if (record.SyncStatus != YnabSyncStatus.Unknown || record.LastError != legacyError)
			{
				record.SyncStatus = YnabSyncStatus.Unknown;
				record.LastError = legacyError;
				record.UpdatedAt = now;
				if (await repository.UpdateAsync(record, cancellationToken))
				{
					await PublishChangeAsync(record.Id, CommittedChangeType.Updated);
				}
			}

			return new YnabPushOperation(record.Id, YnabSyncStatus.Unknown, null, string.Empty, string.Empty,
				record.AttemptCount, record.YnabTransactionId, legacyError);
		}

		return ToPushOperation(record);
	}

	public async Task<YnabPushOperationClaim?> TryClaimPushOperationAsync(Guid syncRecordId, CancellationToken cancellationToken)
	{
		DateTimeOffset now = timeProvider.GetUtcNow();
		Guid claimToken = Guid.NewGuid();
		YnabSyncRecordEntity? claimed = await repository.TryClaimPushOperationAsync(
			syncRecordId,
			claimToken,
			now,
			now - ClaimLease,
			cancellationToken);
		if (claimed is null)
		{
			return null;
		}

		await PublishChangeAsync(claimed.Id, CommittedChangeType.Updated);
		return new YnabPushOperationClaim(ToPushOperation(claimed), claimToken);
	}

	public async Task<bool> CompletePushOperationAsync(
		Guid syncRecordId,
		Guid claimToken,
		YnabSyncStatus status,
		string? ynabTransactionId,
		string? lastError,
		CancellationToken cancellationToken)
	{
		if (status is not (YnabSyncStatus.Synced or YnabSyncStatus.Failed or YnabSyncStatus.Unknown))
		{
			throw new ArgumentOutOfRangeException(nameof(status), status, "A claimed push can only complete as Synced, Failed, or Unknown.");
		}

		bool completed = await repository.CompletePushOperationAsync(
			syncRecordId,
			claimToken,
			status,
			ynabTransactionId,
			lastError,
			timeProvider.GetUtcNow(),
			cancellationToken);
		if (completed)
		{
			await PublishChangeAsync(syncRecordId, CommittedChangeType.Updated);
		}

		return completed;
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

		bool hasUnknown = false;
		bool hasFailed = false;
		bool hasPending = false;
		bool hasSynced = false;

		foreach (YnabSyncRecordEntity record in primaryRecords)
		{
			switch (record.SyncStatus)
			{
				case YnabSyncStatus.Unknown:
					hasUnknown = true;
					break;
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

		if (hasUnknown)
		{
			return ReceiptSyncStatusValue.Unknown;
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

	private static YnabPushOperation ToPushOperation(YnabSyncRecordEntity entity)
	{
		if (entity.RequestPayloadJson is null || entity.PayloadHash is null || entity.SourceVersion is null)
		{
			throw new InvalidOperationException($"YNAB sync record {entity.Id} has no immutable push snapshot.");
		}

		string actualHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entity.RequestPayloadJson)));
		if (!CryptographicOperations.FixedTimeEquals(
			Encoding.ASCII.GetBytes(actualHash),
			Encoding.ASCII.GetBytes(entity.PayloadHash)))
		{
			throw new InvalidOperationException($"YNAB sync record {entity.Id} has a corrupt immutable push snapshot.");
		}

		YnabCreateTransactionRequest request = JsonSerializer.Deserialize<YnabCreateTransactionRequest>(
			entity.RequestPayloadJson,
			PayloadJsonOptions) ?? throw new InvalidOperationException($"YNAB sync record {entity.Id} has an invalid immutable push snapshot.");
		return new YnabPushOperation(
			entity.Id,
			entity.SyncStatus,
			request,
			entity.SourceVersion,
			entity.PayloadHash,
			entity.AttemptCount,
			entity.YnabTransactionId,
			entity.LastError);
	}

	private Task PublishChangeAsync(Guid id, CommittedChangeType changeType) =>
		committedChangePublisher.PublishAsync(new CommittedEntityChange(
			CommittedEntityType.YnabSyncRecord,
			changeType,
			id));
}
