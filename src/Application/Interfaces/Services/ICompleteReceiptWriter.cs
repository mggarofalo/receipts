using Application.Commands.Receipt.CreateComplete;

namespace Application.Interfaces.Services;

/// <summary>
/// Persists a complete receipt as one atomic operation. Receipt policy belongs to the
/// application command handler; implementations own only mapping and durable storage.
/// </summary>
public interface ICompleteReceiptWriter
{
	Task<CreateCompleteReceiptResult> CreateAsync(
		Domain.Core.Receipt receipt,
		List<Domain.Core.Transaction> transactions,
		List<Domain.Core.ReceiptItem> items,
		List<Domain.Core.Adjustment> adjustments,
		CancellationToken cancellationToken);
}
