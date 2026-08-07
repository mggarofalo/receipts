using System.Buffers;
using System.Security.Cryptography;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

/// <summary>
/// Ensures the ONNX embedding model is present on disk, downloading it once if it is not.
///
/// The model is not shipped in the container image (RECEIPTS-929), so on a fresh volume this
/// is what puts it there. It runs in the background: the API serves traffic immediately and
/// <see cref="OnnxEmbeddingService"/> reports <c>IsConfigured == false</c> until the download
/// lands, which every consumer already handles. Failures are logged and retried — they never
/// take the host down, because a HuggingFace outage must not stop the app from starting.
/// </summary>
public sealed class EmbeddingModelProvisioningService(
	IOptions<EmbeddingModelOptions> options,
	IHttpClientFactory httpClientFactory,
	ILogger<EmbeddingModelProvisioningService> logger) : BackgroundService
{
	public const string HttpClientName = "embedding-model-download";

	private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(15);
	private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);
	private const int CopyBufferSize = 128 * 1024;

	private readonly EmbeddingModelOptions _options = options.Value;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		string directory = _options.ResolveModelDirectory();

		if (IsProvisioned(directory))
		{
			logger.LogInformation(
				"Embedding model already provisioned at {Directory} (revision {Revision})",
				directory,
				EmbeddingModelOptions.Revision);
			return;
		}

		if (!_options.AutoDownload)
		{
			logger.LogWarning(
				"Embedding model is missing from {Directory} and automatic download is disabled. " +
				"Semantic features stay disabled until the files are staged in manually.",
				directory);
			return;
		}

		TimeSpan delay = InitialRetryDelay;

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await ProvisionAsync(directory, stoppingToken);

				logger.LogInformation(
					"Embedding model provisioned at {Directory} (revision {Revision})",
					directory,
					EmbeddingModelOptions.Revision);
				return;
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				logger.LogError(
					ex,
					"Failed to provision the embedding model into {Directory}. Retrying in {Delay}. " +
					"Semantic features stay disabled until this succeeds.",
					directory,
					delay);
			}

			try
			{
				await Task.Delay(delay, stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}

			// Exponential backoff, capped so a long upstream outage settles into an
			// occasional retry rather than either hammering the host or giving up.
			delay = delay < MaxRetryDelay
				? TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks))
				: MaxRetryDelay;
		}
	}

	/// <summary>
	/// Cheap startup check: the marker names the revision that was verified, and each file's
	/// length is compared against the expected size. Re-hashing 1.34 GB on every boot would
	/// cost seconds for no real benefit — the digest is verified when the file is written.
	/// </summary>
	internal static bool IsProvisioned(string directory) =>
		IsProvisioned(directory, EmbeddingModelOptions.Files);

	internal static bool IsProvisioned(string directory, IReadOnlyList<EmbeddingModelFile> files)
	{
		string markerPath = Path.Combine(directory, EmbeddingModelOptions.MarkerFileName);

		if (!File.Exists(markerPath))
		{
			return false;
		}

		string marker;
		try
		{
			marker = File.ReadAllText(markerPath).Trim();
		}
		catch (IOException)
		{
			return false;
		}

		if (!string.Equals(marker, EmbeddingModelOptions.Revision, StringComparison.Ordinal))
		{
			return false;
		}

		foreach (EmbeddingModelFile file in files)
		{
			FileInfo info = new(Path.Combine(directory, file.FileName));
			if (!info.Exists || info.Length != file.SizeBytes)
			{
				return false;
			}
		}

		return true;
	}

	private async Task ProvisionAsync(string directory, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(directory);

		HttpClient http = httpClientFactory.CreateClient(HttpClientName);

		foreach (EmbeddingModelFile file in EmbeddingModelOptions.Files)
		{
			cancellationToken.ThrowIfCancellationRequested();

			FileInfo existing = new(Path.Combine(directory, file.FileName));
			if (existing.Exists && existing.Length == file.SizeBytes)
			{
				// Present and the right size — most likely a previous run that got part-way
				// through the set before being interrupted. Leave it alone.
				continue;
			}

			await DownloadAndVerifyAsync(directory, file, http, cancellationToken);
		}

		// Written last: its presence is what lets a later boot skip all of the above.
		await File.WriteAllTextAsync(
			Path.Combine(directory, EmbeddingModelOptions.MarkerFileName),
			EmbeddingModelOptions.Revision,
			cancellationToken);
	}

	private async Task DownloadAndVerifyAsync(
		string directory,
		EmbeddingModelFile file,
		HttpClient http,
		CancellationToken cancellationToken)
	{
		string finalPath = Path.Combine(directory, file.FileName);
		string tempPath = finalPath + ".tmp";
		Uri uri = _options.BuildDownloadUri(file);

		logger.LogInformation(
			"Downloading embedding model file {FileName} ({SizeBytes:N0} bytes) from {Uri}",
			file.FileName,
			file.SizeBytes,
			uri);

		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_options.DownloadTimeout);

		try
		{
			string actualHash;
			long actualSize;

			using (HttpResponseMessage response = await http.GetAsync(
				uri,
				HttpCompletionOption.ResponseHeadersRead,
				timeout.Token))
			{
				// The shell version of this used `curl -sL -o` with no --fail, so an HTTP error
				// wrote the error page into model.onnx and reported success. Fail loudly instead.
				response.EnsureSuccessStatusCode();

				await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token);
				await using FileStream target = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

				using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
				byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

				try
				{
					long total = 0;
					int read;
					while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
					{
						hash.AppendData(buffer, 0, read);
						await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
						total += read;
					}

					actualSize = total;
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(buffer);
				}

				actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
			}

			if (actualSize != file.SizeBytes)
			{
				throw new InvalidOperationException(
					$"{file.FileName} downloaded from {uri} is {actualSize:N0} bytes, expected {file.SizeBytes:N0}.");
			}

			if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException(
					$"{file.FileName} downloaded from {uri} has SHA-256 {actualHash}, expected {file.Sha256}.");
			}

			// Only now does the file appear under its real name, so a torn download can never
			// be picked up as a usable model by OnnxEmbeddingService.
			File.Move(tempPath, finalPath, overwrite: true);
		}
		catch
		{
			TryDeleteTemp(tempPath);
			throw;
		}
	}

	private void TryDeleteTemp(string tempPath)
	{
		try
		{
			File.Delete(tempPath);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			logger.LogWarning(ex, "Could not remove partial download {TempPath}", tempPath);
		}
	}
}
