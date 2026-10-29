namespace Application.Classification;

public sealed record CategoryAssistanceReviewPreference(Guid AttemptId, string State, Guid Revision,
	IReadOnlyList<Guid> ExcludedItemIds);

public sealed record CategoryAssistanceReviewPreferenceRequest(Guid OperationId, Guid ExpectedRevision,
	string State, IReadOnlyList<Guid> ExcludedItemIds);

public sealed record CategoryAssistanceSavedReview(Guid AttemptId, string Location, string Description,
	string ItemCodeKey, string State, Guid Revision, IReadOnlyList<Guid> ExcludedItemIds);
public sealed record CategoryAssistanceSavedReviewPage(int Total, int Offset, int Limit,
	IReadOnlyList<CategoryAssistanceSavedReview> Items);

public interface ICategoryAssistanceReviewPreferenceStore
{
	Task<CategoryAssistanceSavedReviewPage> ListSavedAsync(string actor, int offset, int limit, CancellationToken ct);
	Task<CategoryAssistanceReviewPreference> UpdateSavedAsync(string actor, Guid attemptId,
		CategoryAssistanceReviewPreferenceRequest request, CancellationToken ct);
	Task<CategoryAssistanceReviewPreference> GetAsync(string actor, Guid attemptId, CancellationToken ct);
	Task<CategoryAssistanceReviewPreference> UpdateAsync(string actor, Guid attemptId,
		CategoryAssistanceReviewPreferenceRequest request, CancellationToken ct);
}
