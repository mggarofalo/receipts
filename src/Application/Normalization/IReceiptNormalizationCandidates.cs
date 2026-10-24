namespace Application.Normalization;

/// <summary>Advisory product identity only; receipt examples and catalog operations are not exposed.</summary>
public sealed record ReceiptNormalizationSuggestion(Guid Id, Guid Revision, string CanonicalName,
	string? DisplayLabel, IReadOnlyList<string> Channels);

public interface IReceiptNormalizationCandidates
{
	Task<IReadOnlyList<ReceiptNormalizationSuggestion>> GetCandidatesAsync(string actor,
		NormalizationSourceKey source, CancellationToken cancellationToken);
}
