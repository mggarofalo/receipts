using Common;
using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface IYnabSyncEventRepository
{
	Task<YnabSyncEventEntity> CreateAsync(YnabSyncEventEntity entity, CancellationToken cancellationToken);

	Task<(IReadOnlyList<YnabSyncEventEntity> Events, int TotalCount)> ListAsync(
		int offset,
		int limit,
		YnabSyncStatus? outcome,
		CancellationToken cancellationToken);

	Task<DateTimeOffset?> GetLatestOccurrenceAsync(YnabSyncStatus outcome, CancellationToken cancellationToken);

	Task<int> CountSinceAsync(DateTimeOffset since, YnabSyncStatus? outcome, CancellationToken cancellationToken);
}
