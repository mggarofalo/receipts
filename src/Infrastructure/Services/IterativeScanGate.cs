using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Decides once per process whether the database's pgvector supports <c>hnsw.iterative_scan</c> (0.8+).
/// On an older library the setting is an error, not a no-op, so a production image whose tag floats
/// must not receive it. The installed extension version can only lag the loaded library, so reading
/// <c>pg_extension.extversion</c> never reports support the library lacks.
/// </summary>
internal sealed class IterativeScanGate
{
	internal static readonly Version MinimumVersion = new(0, 8);

	private const int Unknown = 0;
	private const int Supported = 1;
	private const int Unsupported = 2;
	private int _state = Unknown;

	/// <summary>
	/// Reads the version on the first call only and caches the answer; logs one warning when unsupported.
	/// A failed read is not cached, so the next call tries again.
	/// </summary>
	public async Task<bool> IsSupportedAsync(Func<CancellationToken, Task<string?>> readExtensionVersion, ILogger logger,
		CancellationToken cancellationToken)
	{
		int state = Volatile.Read(ref _state);
		if (state != Unknown) { return state == Supported; }
		string? version = await readExtensionVersion(cancellationToken);
		bool supported = Supports(version);
		if (Interlocked.CompareExchange(ref _state, supported ? Supported : Unsupported, Unknown) == Unknown && !supported)
		{
			logger.LogWarning(
				"pgvector extension version {Version} is below {Minimum}; kNN queries run without hnsw.iterative_scan and can return fewer rows than requested while embeddings are stale",
				version ?? "(not installed)", MinimumVersion);
		}
		return supported;
	}

	/// <summary>An unreadable version counts as unsupported: skipping the setting is always safe.</summary>
	internal static bool Supports(string? extensionVersion) =>
		Version.TryParse(extensionVersion, out Version? version) && version >= MinimumVersion;
}
