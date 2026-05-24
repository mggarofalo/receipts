using Application.Models.Ynab;
using Common;

namespace Application.Interfaces.Services;

public interface IYnabSyncEventService
{
	Task RecordAsync(
		YnabSyncType eventType,
		YnabSyncStatus outcome,
		Guid? localTransactionId,
		Guid? receiptId,
		string? ynabBudgetId,
		string? ynabTransactionId,
		string? errorMessage,
		CancellationToken cancellationToken);

	Task<YnabSyncEventsPage> ListAsync(int offset, int limit, YnabSyncStatus? outcome, CancellationToken cancellationToken);

	Task<DateTimeOffset?> GetLatestOccurrenceAsync(YnabSyncStatus outcome, CancellationToken cancellationToken);

	Task<int> CountSinceAsync(DateTimeOffset since, YnabSyncStatus? outcome, CancellationToken cancellationToken);
}
