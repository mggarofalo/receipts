namespace Application.Classification;

public sealed record CategoryAssistanceDecisionRequest(Guid OperationId, Guid ExpectedTaxonomyRevision,
	IReadOnlyList<Guid> ItemIds, Guid? CategoryId = null);
public sealed record CategoryAssistanceDecisionResult(Guid AttemptId, string Decision,
	IReadOnlyList<Guid> AffectedItemIds, Guid? CategoryId);
public sealed record CategoryAssistanceEligibleItemPage(int Total, int Offset, int Limit,
	IReadOnlyList<Guid> ItemIds);

public interface ICategoryAssistanceDecisionStore
{
	Task<CategoryAssistanceEligibleItemPage> ListEligibleItemsAsync(Guid attemptId, int offset, int limit, CancellationToken ct);
	Task<CategoryAssistanceDecisionResult> AcceptAsync(string actor, Guid attemptId,
		CategoryAssistanceDecisionRequest request, CancellationToken ct);
	Task<CategoryAssistanceDecisionResult> DismissAsync(string actor, Guid attemptId, Guid operationId,
		CancellationToken ct);
}
