namespace Application.Classification;

public sealed record CategoryAssistanceReviewPreference(Guid AttemptId, string State, Guid Revision,
	IReadOnlyList<Guid> ExcludedItemIds);

public sealed record CategoryAssistanceReviewPreferenceRequest(Guid OperationId, Guid ExpectedRevision,
	string State, IReadOnlyList<Guid> ExcludedItemIds);

public interface ICategoryAssistanceReviewPreferenceStore
{
	Task<CategoryAssistanceReviewPreference> GetAsync(string actor, Guid attemptId, CancellationToken ct);
	Task<CategoryAssistanceReviewPreference> UpdateAsync(string actor, Guid attemptId,
		CategoryAssistanceReviewPreferenceRequest request, CancellationToken ct);
}
