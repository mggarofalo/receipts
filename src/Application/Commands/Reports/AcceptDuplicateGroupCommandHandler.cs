using Application.Duplicates;
using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Reports;

public class AcceptDuplicateGroupCommandHandler(IDuplicateAcceptanceStore store, TimeProvider timeProvider)
	: IRequestHandler<AcceptDuplicateGroupCommand, int>
{
	public async ValueTask<int> Handle(AcceptDuplicateGroupCommand request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		List<Guid> ids = [.. request.ReceiptIds.Distinct().Order()];
		if (ids.Count < 2)
		{
			return 0;
		}

		DuplicateAcceptanceWriteResult result = await store.AcceptAsync(ids, DuplicateAcceptancePolicy.CanonicalPairs(ids), timeProvider.GetUtcNow(), cancellationToken);
		if (result.MissingReceiptIds.Count > 0)
		{
			string sample = string.Join(", ", result.MissingReceiptIds.Take(10));
			string suffix = result.MissingReceiptIds.Count > 10 ? $" (+{result.MissingReceiptIds.Count - 10} more)" : "";
			throw new KeyNotFoundException($"Receipt(s) not found: {sample}{suffix}");
		}
		return result.InsertedPairCount;
	}
}
