namespace Application.Normalization;

public interface IOfflinePortableBackupInspection
{
	Task<OfflineBackupInspection> InspectAsync(string sourcePath, CancellationToken cancellationToken = default);
	void VerifyBaseline(OfflineBackupInspection baseline, OfflineBackupInspection candidate);
	void ValidateManifest(OfflineNormalizationManifest manifest, OfflineBackupInspection source, bool requireReviewed, DateTimeOffset now);
}

/// <summary>Private report, never receipt data on stdout. Digests exclude normalization fields only.</summary>
public sealed record OfflineBackupInspection(int InspectionVersion, string SourceSha256, int FormatVersion,
	string FactualBaselineSha256, IReadOnlyDictionary<string, string> FactualTableSha256,
	IReadOnlyDictionary<string, long> RowCounts, IReadOnlyList<OfflineCurrencyTotal> CurrencyTotals,
	IReadOnlyDictionary<string, long> ReviewStateCounts, int ScopedSourceCount, IReadOnlyList<OfflineSourceObservation> Observations,
	IReadOnlyList<OfflineCanonicalObservation> CanonicalTargets);
public sealed record OfflineCurrencyTotal(string Kind, string Currency, decimal Amount);
public sealed record OfflineSourceObservation(string EntityKind, Guid EntityId, NormalizationSourceKey Source,
	string SourceHash, Guid? LinkedCanonicalId, string ReviewState);
public sealed record OfflineCanonicalObservation(Guid Id, string CanonicalName, string? DisplayLabel, string Status);
