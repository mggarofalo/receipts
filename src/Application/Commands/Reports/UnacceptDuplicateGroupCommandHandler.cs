using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Reports;

public class UnacceptDuplicateGroupCommandHandler(IDuplicateAcceptanceStore store)
	: IRequestHandler<UnacceptDuplicateGroupCommand, int>
{
	public async ValueTask<int> Handle(UnacceptDuplicateGroupCommand request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		List<Guid> ids = [.. request.ReceiptIds.Distinct()];
		return ids.Count < 2 ? 0 : await store.RemovePairsAsync(ids, cancellationToken);
	}
}
