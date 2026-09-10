using Common;
using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface IYnabSyncRecordRepository
{
	Task<YnabSyncRecordEntity> CreateAsync(YnabSyncRecordEntity entity, CancellationToken cancellationToken);
	Task<YnabSyncRecordEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<YnabSyncRecordEntity?> GetByTransactionTypeAndBudgetAsync(Guid localTransactionId, YnabSyncType syncType, string ynabBudgetId, CancellationToken cancellationToken);
	Task<PreparedYnabPushOperation> GetOrCreatePushOperationAsync(YnabSyncRecordEntity proposed, CancellationToken cancellationToken);
	Task<YnabSyncRecordEntity?> TryClaimPushOperationAsync(Guid id, Guid claimToken, DateTimeOffset now, DateTimeOffset staleBefore, CancellationToken cancellationToken);
	Task<bool> CompletePushOperationAsync(Guid id, Guid claimToken, YnabSyncStatus status, string? ynabTransactionId, string? lastError, DateTimeOffset now, CancellationToken cancellationToken);
	Task<bool> UpdateAsync(YnabSyncRecordEntity entity, CancellationToken cancellationToken);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken);
	Task<List<YnabSyncRecordEntity>> GetByReceiptIdsAndBudgetAsync(List<Guid> receiptIds, string ynabBudgetId, CancellationToken cancellationToken);
	Task<DateTimeOffset?> GetLatestSuccessfulSyncTimestampAsync(string ynabBudgetId, CancellationToken cancellationToken);
}
