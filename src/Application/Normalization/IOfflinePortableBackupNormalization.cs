namespace Application.Normalization;

public interface IOfflinePortableBackupNormalization
{
	Task<OfflineBackupNormalizationResult> ApplyAsync(string sourcePath, string manifestPath, string outputPath, CancellationToken cancellationToken = default);
	Task<OfflineBackupNormalizationResult> VerifyAsync(string sourcePath, string manifestPath, string outputPath, CancellationToken cancellationToken = default);
}

public sealed record OfflineBackupNormalizationResult(string OutputPath, string OutputSha256, string SourceSha256,
	string ManifestSha256, bool Unchanged, IReadOnlyDictionary<string, long> ReviewStateCounts);
