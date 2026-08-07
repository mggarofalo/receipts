namespace Infrastructure.Services;

/// <summary>
/// Describes one file that makes up the embedding model, together with the size and
/// digest it must have once downloaded.
/// </summary>
/// <param name="FileName">Name the file is stored under in the model directory.</param>
/// <param name="RemotePath">Path relative to the pinned revision root on the remote host.</param>
/// <param name="SizeBytes">Exact expected size, used as a cheap truncation guard on startup.</param>
/// <param name="Sha256">Expected SHA-256, verified while the file is being written.</param>
public sealed record EmbeddingModelFile(string FileName, string RemotePath, long SizeBytes, string Sha256);

/// <summary>
/// Where the ONNX embedding model lives and how it is obtained.
///
/// The model is deliberately NOT part of the build output or the container image — at
/// 1.34 GB it dwarfed everything else we ship (RECEIPTS-929). It is provisioned into a
/// persistent directory at runtime by <see cref="EmbeddingModelProvisioningService"/>
/// and loaded lazily by <see cref="OnnxEmbeddingService"/>.
/// </summary>
public sealed class EmbeddingModelOptions
{
	public const string SectionName = "Embeddings";

	/// <summary>
	/// Pinned upstream commit. HuggingFace branch refs are mutable — a re-upload to
	/// <c>main</c> would silently change the model under us and invalidate every
	/// embedding already stored in the database, so we address a specific revision.
	/// </summary>
	public const string Revision = "d4aa6901d3a41ba39fb536a557fa166f842b0e09";

	public const string ModelFileName = "model.onnx";
	public const string VocabFileName = "vocab.txt";

	/// <summary>
	/// Written next to the model files once both have been verified. Holds
	/// <see cref="Revision"/> so that bumping the pinned revision re-provisions.
	/// </summary>
	public const string MarkerFileName = ".provisioned";

	private const string ModelDirectoryName = "BgeLargeEnV15";

	/// <summary>
	/// Digests come from the HuggingFace LFS metadata for <see cref="Revision"/>
	/// (the LFS object id is the SHA-256), so no full download was needed to obtain them.
	/// </summary>
	public static readonly IReadOnlyList<EmbeddingModelFile> Files =
	[
		new(ModelFileName, "onnx/model.onnx", 1_336_854_281L, "69ed3f810d3b6d13f70dff9ca89966f39c0a0e877fb88211be7bcc070df2a2ce"),
		new(VocabFileName, "vocab.txt", 231_508L, "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"),
	];

	/// <summary>
	/// Directory holding <c>model.onnx</c> and <c>vocab.txt</c>. When unset, falls back to a
	/// per-machine cache under the user's local application data — one download per machine,
	/// shared by every clone and worktree. Containers set this to a path on a mounted volume.
	/// </summary>
	public string? ModelPath { get; set; }

	/// <summary>
	/// When false the app never reaches out to the network; the model must already be present.
	/// Set this for air-gapped deployments that stage the files in by hand.
	/// </summary>
	public bool AutoDownload { get; set; } = true;

	/// <summary>
	/// Root the pinned revision is appended to. Overridable so a deployment can point at an
	/// internal mirror instead of HuggingFace.
	/// </summary>
	public string BaseUrl { get; set; } = "https://huggingface.co/BAAI/bge-large-en-v1.5/resolve";

	/// <summary>Cap on how long a single file download may take.</summary>
	public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(30);

	public string ResolveModelDirectory()
	{
		if (!string.IsNullOrWhiteSpace(ModelPath))
		{
			return ModelPath;
		}

		string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

		// GetFolderPath returns empty when the platform cannot resolve the folder (e.g. a
		// container with no HOME set). Fall back to the XDG default rather than producing
		// a relative path that would land inside whatever the current directory happens to be.
		if (string.IsNullOrWhiteSpace(root))
		{
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			root = string.IsNullOrWhiteSpace(home)
				? Path.Combine(Path.GetTempPath(), "Receipts")
				: Path.Combine(home, ".local", "share");
		}

		return Path.Combine(root, "Receipts", "models", ModelDirectoryName);
	}

	public Uri BuildDownloadUri(EmbeddingModelFile file) =>
		new($"{BaseUrl.TrimEnd('/')}/{Revision}/{file.RemotePath}");
}
