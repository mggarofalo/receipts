using Common;
using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface IYnabSyncRecordRepository
{
	Task<YnabSyncRecordEntity> CreateAsync(YnabSyncRecordEntity entity, CancellationToken cancellationToken);
	Task<YnabSyncRecordEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<YnabSyncRecordEntity?> GetByTransactionTypeAndBudgetAsync(Guid localTransactionId, YnabSyncType syncType, string ynabBudgetId, CancellationToken cancellationToken);
	Task<bool> UpdateAsync(YnabSyncRecordEntity entity, CancellationToken cancellationToken);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken);
	Task<List<YnabSyncRecordEntity>> GetByReceiptIdsAndBudgetAsync(List<Guid> receiptIds, string ynabBudgetId, CancellationToken cancellationToken);
	Task<DateTimeOffset?> GetLatestSuccessfulSyncTimestampAsync(string ynabBudgetId, CancellationToken cancellationToken);
}
