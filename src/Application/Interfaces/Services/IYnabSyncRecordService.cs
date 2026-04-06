using Application.Models.Ynab;
using Common;

namespace Application.Interfaces.Services;

public interface IYnabSyncRecordService
{
	Task<YnabSyncRecordDto> CreateAsync(Guid localTransactionId, string ynabBudgetId, YnabSyncType syncType, CancellationToken cancellationToken);
	Task<YnabSyncRecordDto?> GetByTransactionAndTypeAsync(Guid localTransactionId, YnabSyncType syncType, CancellationToken cancellationToken);
	Task UpdateStatusAsync(Guid id, YnabSyncStatus status, string? ynabTransactionId, string? lastError, CancellationToken cancellationToken);
	Task<List<ReceiptYnabSyncStatusDto>> GetSyncStatusesByReceiptIdsAsync(List<Guid> receiptIds, CancellationToken cancellationToken);
}
