using Application.Models;
using Application.Models.Ynab;
using Common;

namespace Application.Interfaces.Services;

/// <summary>
/// Reads and writes the append-only YNAB sync-event log backing the /ynab status page.
/// </summary>
public interface IYnabSyncEventService
{
	/// <summary>Append one event for a YNAB attempt. User id is resolved from the current request.</summary>
	Task WriteAsync(
		YnabSyncEventType eventType,
		bool success,
		Guid? receiptId = null,
		Guid? transactionId = null,
		int? httpStatus = null,
		string? errorMessage = null,
		string? requestId = null,
		CancellationToken cancellationToken = default);

	/// <summary>Paginated, filterable feed of recent events (most recent first by default).</summary>
	Task<PagedResult<YnabSyncEventDto>> GetRecentAsync(
		int offset,
		int limit,
		SortParams sort,
		bool? success = null,
		DateTimeOffset? dateFrom = null,
		DateTimeOffset? dateTo = null,
		CancellationToken cancellationToken = default);

	/// <summary>Aggregate stats (push counts by window, last success/failure/validate timestamps).</summary>
	Task<YnabStatus> GetStatusAsync(bool isConfigured, CancellationToken cancellationToken = default);
}
