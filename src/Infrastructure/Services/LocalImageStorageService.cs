using Application.Interfaces.Services;
using Application.Models.Images;
using Common;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Services;

public class LocalImageStorageService : IImageStorageService
{
	private readonly IConfiguration _configuration;
	private readonly Func<string, byte[], CancellationToken, Task> _atomicWriter;

	public LocalImageStorageService(IConfiguration configuration)
		: this(configuration, WriteAtomicAsync)
	{
	}

	internal LocalImageStorageService(
		IConfiguration configuration,
		Func<string, byte[], CancellationToken, Task> atomicWriter)
	{
		_configuration = configuration;
		_atomicWriter = atomicWriter;
	}

	private string StorageRoot =>
		_configuration[ConfigurationVariables.ImageStoragePath]
		?? Path.Combine(AppContext.BaseDirectory, "ImageStorage");

	public async Task<ReceiptImageSet> SaveImageSetAsync(
		Guid receiptId,
		byte[] originalBytes,
		string originalExtension,
		byte[] processedBytes,
		CancellationToken ct)
	{
		if (originalExtension.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
		{
			throw new ArgumentException("Image extension must not contain a directory separator.", nameof(originalExtension));
		}

		string receiptDirectory = GetReceiptDirectory(receiptId);
		Directory.CreateDirectory(receiptDirectory);

		string version = $"set-{Guid.NewGuid():N}";
		string stagingDirectory = Path.Combine(receiptDirectory, $".staging-{version}");
		string publishedDirectory = Path.Combine(receiptDirectory, version);
		Directory.CreateDirectory(stagingDirectory);

		string originalFileName = $"original{originalExtension}";
		string originalPath = Path.Combine(stagingDirectory, originalFileName);
		string processedPath = Path.Combine(stagingDirectory, "processed.png");

		try
		{
			// Keep the existing per-file atomic write guarantee inside the staging directory,
			// then publish the complete pair with one same-volume directory rename. Readers can
			// therefore observe neither half of a new image set.
			await _atomicWriter(originalPath, originalBytes, ct);
			await _atomicWriter(processedPath, processedBytes, ct);
			Directory.Move(stagingDirectory, publishedDirectory);
		}
		catch
		{
			TryDeleteDirectory(stagingDirectory);
			throw;
		}

		return new(
			Path.Combine(receiptId.ToString(), version, originalFileName),
			Path.Combine(receiptId.ToString(), version, "processed.png"));
	}

	// Writes bytes to a temp file in the SAME directory as finalPath and then atomically promotes
	// it into place via File.Move(overwrite: true). A rename on the same volume is atomic, so a
	// consumer of finalPath always observes either the complete previous content or the complete
	// new content — never a truncated/half-written file. This replaces the previous in-place
	// File.WriteAllBytesAsync(finalPath, ...), which opened finalPath with FileMode.Create
	// (truncate-then-write): an I/O failure or cancellation mid-write left the existing image
	// truncated even though the overall upload failed. On any failure here the temp file is deleted
	// (best-effort) and the exception is rethrown, so a pre-existing image is never corrupted.
	private static async Task WriteAtomicAsync(string finalPath, byte[] bytes, CancellationToken ct)
	{
		string tempPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";
		try
		{
			await File.WriteAllBytesAsync(tempPath, bytes, ct);
			File.Move(tempPath, finalPath, overwrite: true);
		}
		catch
		{
			TryDeleteTempFile(tempPath);
			throw;
		}
	}

	private static void TryDeleteTempFile(string tempPath)
	{
		try
		{
			if (File.Exists(tempPath))
			{
				File.Delete(tempPath);
			}
		}
		catch (IOException)
		{
			// Best-effort cleanup: a leftover temp file is harmless and must not mask the write failure.
		}
		catch (UnauthorizedAccessException)
		{
			// Best-effort cleanup: a leftover temp file is harmless and must not mask the write failure.
		}
	}

	private static void TryDeleteDirectory(string directory)
	{
		try
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
		catch (IOException)
		{
			// Best-effort. The orphan sweeper retries abandoned staging/version directories.
		}
		catch (UnauthorizedAccessException)
		{
			// Best-effort. The orphan sweeper reports a later failure if access remains denied.
		}
	}

	public string GetImagePath(Guid receiptId, string fileName)
	{
		return Path.Combine(GetReceiptDirectory(receiptId), fileName);
	}

	public Task DeleteReceiptImagesAsync(Guid receiptId, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		string directory = GetReceiptDirectory(receiptId);
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
		return Task.CompletedTask;
	}

	public Task<ImageCleanupResult> CleanupUnreferencedAsync(
		IReadOnlySet<string> referencedPaths,
		DateTimeOffset createdBefore,
		CancellationToken ct)
	{
		string root = Path.GetFullPath(StorageRoot);
		if (!Directory.Exists(root))
		{
			return Task.FromResult(new ImageCleanupResult(0, 0));
		}

		HashSet<string> referenced = new(PathComparer);
		foreach (string relativePath in referencedPaths)
		{
			try
			{
				string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
				if (IsWithin(root, fullPath))
				{
					referenced.Add(fullPath);
				}
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
			{
				// An invalid legacy/backup path is not owned by this storage root and cannot
				// protect arbitrary filesystem content from cleanup.
			}
		}

		int deletedSets = 0;
		int deletedReceiptDirectories = 0;
		int failedEntries = 0;
		foreach (string receiptDirectory in Directory.GetDirectories(root))
		{
			ct.ThrowIfCancellationRequested();
			if (!Guid.TryParse(Path.GetFileName(receiptDirectory), out _))
			{
				continue;
			}

			try
			{
				// Never recurse through a link whose GUID-shaped name makes it look like an
				// owned receipt directory. Local filesystem manipulation must not broaden
				// cleanup beyond the configured storage tree.
				if (IsReparsePoint(receiptDirectory))
				{
					failedEntries++;
					continue;
				}

				foreach (string setDirectory in Directory.GetDirectories(receiptDirectory))
				{
					ct.ThrowIfCancellationRequested();
					try
					{
						bool isReferenced = referenced.Any(path => IsWithin(setDirectory, path));
						if (!isReferenced
							&& !IsReparsePoint(setDirectory)
							&& Directory.GetLastWriteTimeUtc(setDirectory) <= createdBefore.UtcDateTime)
						{
							Directory.Delete(setDirectory, recursive: true);
							deletedSets++;
						}
						else if (!isReferenced && IsReparsePoint(setDirectory))
						{
							failedEntries++;
						}
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
					{
						failedEntries++;
					}
				}

				// Legacy layouts kept both files directly under the receipt directory. Delete
				// only unreferenced files; never remove the root as an image set because it may
				// also contain a newly published version directory.
				foreach (string file in Directory.GetFiles(receiptDirectory))
				{
					ct.ThrowIfCancellationRequested();
					try
					{
						if (!referenced.Contains(Path.GetFullPath(file))
							&& File.GetLastWriteTimeUtc(file) <= createdBefore.UtcDateTime)
						{
							File.Delete(file);
						}
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
					{
						failedEntries++;
					}
				}

				if (!Directory.EnumerateFileSystemEntries(receiptDirectory).Any())
				{
					Directory.Delete(receiptDirectory);
					deletedReceiptDirectories++;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// A persistent failure in one receipt must not starve every later orphan.
				failedEntries++;
			}
		}

		return Task.FromResult(new ImageCleanupResult(deletedSets, deletedReceiptDirectories, failedEntries));
	}

	private string GetReceiptDirectory(Guid receiptId) => Path.Combine(StorageRoot, receiptId.ToString());

	private static bool IsWithin(string directory, string path)
	{
		string relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
		return relative != ".."
			&& !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
			&& !Path.IsPathRooted(relative);
	}

	private static bool IsReparsePoint(string path)
		=> (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

	private static StringComparer PathComparer => OperatingSystem.IsWindows()
		? StringComparer.OrdinalIgnoreCase
		: StringComparer.Ordinal;
}
