using Application.Models.Ynab;
using Common;

namespace Application.Interfaces.Services;

public interface IYnabSyncRecordService
{
	Task<YnabSyncRecordDto> CreateAsync(Guid localTransactionId, string ynabBudgetId, YnabSyncType syncType, CancellationToken cancellationToken);
	Task<YnabSyncRecordDto?> GetByTransactionTypeAndBudgetAsync(Guid localTransactionId, YnabSyncType syncType, string ynabBudgetId, CancellationToken cancellationToken);
	Task<YnabPushOperation> PreparePushOperationAsync(Guid localTransactionId, string ynabBudgetId, YnabCreateTransactionRequest request, string sourceVersion, CancellationToken cancellationToken);
	Task<YnabPushOperationClaim?> TryClaimPushOperationAsync(Guid syncRecordId, CancellationToken cancellationToken);
	Task<bool> CompletePushOperationAsync(Guid syncRecordId, Guid claimToken, YnabSyncStatus status, string? ynabTransactionId, string? lastError, CancellationToken cancellationToken);
	Task UpdateStatusAsync(Guid id, YnabSyncStatus status, string? ynabTransactionId, string? lastError, CancellationToken cancellationToken);
	Task<List<ReceiptYnabSyncStatusDto>> GetSyncStatusesByReceiptIdsAndBudgetAsync(List<Guid> receiptIds, string ynabBudgetId, CancellationToken cancellationToken);
	Task<DateTimeOffset?> GetLatestSuccessfulSyncTimestampAsync(string ynabBudgetId, CancellationToken cancellationToken);
}
