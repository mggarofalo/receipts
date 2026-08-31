using Application.Commands.Receipt.CreateComplete;

namespace Application.Interfaces.Services;

public interface ICompleteReceiptService
{
	Task<CreateCompleteReceiptResult> CreateAsync(
		Domain.Core.Receipt receipt,
		List<Domain.Core.Transaction> transactions,
		List<Domain.Core.ReceiptItem> items,
		List<Domain.Core.Adjustment> adjustments,
		CancellationToken cancellationToken);
}
