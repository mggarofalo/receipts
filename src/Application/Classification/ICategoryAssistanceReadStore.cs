using Application.Normalization;

namespace Application.Classification;

public sealed record CategoryAssistanceSuggestionRead(Guid AttemptId, NormalizationSourceKey Source,
	string? OriginalDescription, int AffectedCount, IReadOnlyList<Guid> ProposedPath,
	string? AbstentionReason, double? Confidence, string State, bool IsCurrent,
		DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
	double? FitProbability, double? CategoryProbability)
{
	public string ReviewState { get; init; } = "active";
	public Guid ReviewRevision { get; init; }
	public int ExcludedItemCount { get; init; }
}

public sealed record CategoryAssistanceSuggestionPage(int Total, int Offset, int Limit,
	IReadOnlyList<CategoryAssistanceSuggestionRead> Items);

public sealed record CategoryAssistanceActivity(int Queued, int Dispatching, int Suggested,
	int Abstained, int Failed, int Stale, int Indeterminate, int TodayAttempts);

/// <summary>Read-only operational projection. Never invokes an inference provider.</summary>
public interface ICategoryAssistanceReadStore
{
	Task<CategoryAssistanceSuggestionPage> ListSuggestionsAsync(int offset, int limit, CancellationToken ct);
	Task<CategoryAssistanceSuggestionPage> ListSuggestionsAsync(string actor, string reviewFilter,
		int offset, int limit, CancellationToken ct);
	Task<CategoryAssistanceSuggestionRead?> GetSuggestionAsync(Guid attemptId, CancellationToken ct);
	Task<CategoryAssistanceSuggestionRead?> GetSuggestionAsync(string actor, Guid attemptId, CancellationToken ct);
	Task<CategoryAssistanceActivity> GetActivityAsync(CancellationToken ct);
}
