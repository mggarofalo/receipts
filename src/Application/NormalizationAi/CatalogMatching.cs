using Application.Classification;

namespace Application.NormalizationAi;

/// <summary>
/// One shortlisted catalog name. DisplayName is what a person sees (DisplayLabel, else CanonicalName), which the
/// database keeps unique case-insensitively. Approved examples are the candidate's approved source descriptions, each
/// marked same-scope when it shares the purchase's store and item code; usually there are none.
/// </summary>
public sealed record CatalogMatchCandidate(Guid Id, string DisplayName, IReadOnlyList<NormalizationApprovedExample> ApprovedExamples);

/// <summary>What a person sees on the receipt line (the item text and, when known, the store) and the shortlist to choose from.</summary>
public sealed record CatalogMatchRequest(string Description, string? Store, IReadOnlyList<CatalogMatchCandidate> Candidates)
{
	/// <summary>
	/// The same request with every approved example of <paramref name="description"/> removed from every candidate, so an
	/// evaluated source is never its own evidence (leave-one-out). Candidates and their order are unchanged.
	/// </summary>
	public CatalogMatchRequest WithoutExamplesOf(string description) => this with
	{
		Candidates = [.. Candidates.Select(c => c with
		{
			ApprovedExamples = [.. c.ApprovedExamples.Where(e => !string.Equals(e.Description, description, StringComparison.Ordinal))]
		})]
	};
}

/// <summary>
/// Input rules of <see cref="Version"/> (docs/jev-catalog-matching.md). The limits are category assistance's, because
/// both policies share one Jev connection and its per-request bounds.
/// </summary>
public static class CatalogMatchingPolicy
{
	public const string Version = "catalog-matching-questions-v1";
	public const string Model = CategorySuggestionPolicy.Model;
	public const int MaximumOptions = CategorySuggestionPolicy.MaximumOptions;
	public const int MaximumPayloadBytes = CategorySuggestionPolicy.MaximumPayloadBytes;

	/// <summary>
	/// A nonempty purchase and 1–255 candidates with distinct IDs and display names that are distinct ignoring case.
	/// An empty shortlist is not a request: the caller routes it to naming without asking Jev.
	/// </summary>
	public static bool IsValid(CatalogMatchRequest request)
	{
		if (request is null || string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > MaximumPayloadBytes ||
			(request.Store is not null && (string.IsNullOrWhiteSpace(request.Store) || request.Store.Length > MaximumPayloadBytes)) ||
			request.Candidates is null || request.Candidates.Count is < 1 or > MaximumOptions)
		{
			return false;
		}

		HashSet<Guid> ids = [];
		HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
		return request.Candidates.All(c => c is not null && c.Id != Guid.Empty && ids.Add(c.Id) &&
			!string.IsNullOrWhiteSpace(c.DisplayName) && c.DisplayName.Length <= MaximumPayloadBytes && names.Add(c.DisplayName) &&
			c.ApprovedExamples is not null && c.ApprovedExamples.All(e => e is not null && !string.IsNullOrWhiteSpace(e.Description)));
	}
}
