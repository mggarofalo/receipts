using Application.Normalization;

namespace Infrastructure.IntegrationTests.CatalogRecall;

/// <summary>Where a case's expected target comes from. The two are never mixed in one number.</summary>
public enum CatalogRecallGroundTruth
{
	/// <summary>Active, non-excluded <c>NormalizationSourceMappings</c>: human-approved.</summary>
	ApprovedMapping,

	/// <summary>
	/// Proxy, not human-approved: purchases the legacy resolver linked (origin <c>legacy_unknown</c>).
	/// Many canonical names were created from the first receipt text seen, so <c>exact</c> and
	/// <c>semantic</c> look better here than they are; read the exact-name strata side by side.
	/// </summary>
	LegacyLink,
}

public sealed record CatalogRecallCase(NormalizationSourceKey Source, Guid TargetId, string TargetCanonicalName,
	string? TargetDisplayLabel, Guid? MappingId)
{
	public string TargetDisplayName => TargetDisplayLabel ?? TargetCanonicalName;
}

/// <summary>Where the expected target landed in one mode's shortlist.</summary>
/// <param name="Rank">1-based position in the final shortlist, or null when it is not there.</param>
/// <param name="Channels">Channels that carried the target into the shortlist; empty on a miss.</param>
/// <param name="PoolChannels">Channels that found the target before final ordering and the 20-row cut.</param>
/// <param name="RawSemanticRank">1-based rank among the 20 unfiltered nearest neighbours, or null.</param>
public sealed record CatalogRecallOutcome(int? Rank, IReadOnlyList<string> Channels, IReadOnlyList<string> PoolChannels,
	int? RawSemanticRank, IReadOnlyList<string> Strata, string Store);
