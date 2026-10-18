using Application.Duplicates;
namespace Application.Interfaces.Services;

public interface IDuplicateAcceptanceStore
{
	Task<DuplicateAcceptanceWriteResult> AcceptAsync(IReadOnlyList<Guid> receiptIds, IReadOnlyList<(Guid A, Guid B)> pairs, DateTimeOffset acceptedAt, CancellationToken cancellationToken);
	Task<int> RemovePairsAsync(List<Guid> receiptIds, CancellationToken cancellationToken);
}
