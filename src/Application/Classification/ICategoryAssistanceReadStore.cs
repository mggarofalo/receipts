using System.Text.Json.Serialization;
using Application.Normalization;

namespace Application.Classification;

public sealed record CategoryAssistanceSuggestionRead(Guid AttemptId, NormalizationSourceKey Source,
	string? OriginalDescription, int AffectedCount, IReadOnlyList<Guid> ProposedPath,
	string? AbstentionReason, double? Confidence, string State, bool IsCurrent,
		DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
	double? FitProbability, double? CategoryProbability)
{
	public double? SelectedPathSupport { get; init; }
	public string ReviewState { get; init; } = "active";
	public Guid ReviewRevision { get; init; }
	public int ExcludedItemCount { get; init; }
	[JsonRequired]
	public string ProbabilityScope { get; init; } = "global_choice";
	/// <summary>Stage-1 top roots and stage-2 top choices with their own stage's probability; empty for older policies.</summary>
	[JsonRequired]
	public IReadOnlyList<CategoryAssistanceCandidateRead> RootCandidates { get; init; } = [];
	[JsonRequired]
	public IReadOnlyList<CategoryAssistanceCandidateRead> LeafCandidates { get; init; } = [];
}

public sealed record CategoryAssistanceCandidateRead(Guid CategoryId, double Probability);

public sealed record CategoryAssistanceSuggestionPage(int Total, int Offset, int Limit,
	IReadOnlyList<CategoryAssistanceSuggestionRead> Items);

public sealed record CategoryAssistanceFailureRead(Guid AttemptId, string Failure, DateTimeOffset CompletedAt);

public sealed record CategoryAssistanceActivity(int Queued, int Dispatching, int Suggested,
	int Abstained, int Failed, int Stale, int Indeterminate, int TodayAttempts)
{
	[JsonRequired]
	public IReadOnlyList<CategoryAssistanceFailureRead> RecentFailures { get; init; } = [];
	/// <summary>Strong matches written automatically; they no longer wait for review.</summary>
	[JsonRequired]
	public int Applied { get; init; }
}

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
