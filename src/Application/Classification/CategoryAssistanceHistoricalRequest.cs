using Application.Taxonomy;

namespace Application.Classification;

public sealed record CategoryAssistanceHistoricalRequest(Guid OperationId, Guid TaxonomyRevisionId,
	IReadOnlyList<HistoricalGroupKey> Groups);

public sealed record CategoryAssistanceHistoricalGroupProgress(HistoricalGroupKey Group, int ItemCount,
	IReadOnlyList<Guid> AttemptIds, IReadOnlyList<string> States);

public sealed record CategoryAssistanceHistoricalProgress(Guid OperationId, DateTimeOffset CreatedAt,
	int GroupCount, int ItemCount, string State, IReadOnlyList<CategoryAssistanceHistoricalGroupProgress> Groups);

public sealed record CategoryAssistanceHistoricalPage(int Total, int Offset, int Limit,
	IReadOnlyList<CategoryAssistanceHistoricalProgress> Items);

public interface ICategoryAssistanceHistoricalRequestStore
{
	Task<CategoryAssistanceHistoricalProgress> EnrollAsync(string actor, CategoryAssistanceHistoricalRequest request,
		CancellationToken ct);
	Task<CategoryAssistanceHistoricalProgress> GetAsync(string actor, Guid operationId, CancellationToken ct);
	Task<CategoryAssistanceHistoricalPage> ListAsync(string actor, int offset, int limit, CancellationToken ct);
}
