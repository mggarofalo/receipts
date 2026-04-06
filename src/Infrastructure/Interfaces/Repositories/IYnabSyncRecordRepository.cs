using Common;
using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface IYnabSyncRecordRepository
{
	Task<YnabSyncRecordEntity> CreateAsync(YnabSyncRecordEntity entity, CancellationToken cancellationToken);
	Task<YnabSyncRecordEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<YnabSyncRecordEntity?> GetByTransactionAndTypeAsync(Guid localTransactionId, YnabSyncType syncType, CancellationToken cancellationToken);
	Task UpdateAsync(YnabSyncRecordEntity entity, CancellationToken cancellationToken);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken);
	Task<List<YnabSyncRecordEntity>> GetByReceiptIdsAsync(List<Guid> receiptIds, CancellationToken cancellationToken);
}
