using Application.Interfaces.Services;
using Common;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Services;

public class LocalImageStorageService(IConfiguration configuration) : IImageStorageService
{
	private string StorageRoot =>
		configuration[ConfigurationVariables.ImageStoragePath]
		?? Path.Combine(AppContext.BaseDirectory, "ImageStorage");

	public async Task<string> SaveOriginalAsync(Guid receiptId, byte[] imageBytes, string extension, CancellationToken ct)
	{
		string directory = Path.Combine(StorageRoot, receiptId.ToString());
		Directory.CreateDirectory(directory);

		string fileName = $"original{extension}";
		string filePath = Path.Combine(directory, fileName);

		await WriteAtomicAsync(filePath, imageBytes, ct);

		// Return relative path (receiptId/filename) instead of absolute filesystem path
		return Path.Combine(receiptId.ToString(), fileName);
	}

	public async Task<string> SaveProcessedAsync(Guid receiptId, byte[] processedBytes, CancellationToken ct)
	{
		string directory = Path.Combine(StorageRoot, receiptId.ToString());
		Directory.CreateDirectory(directory);

		string filePath = Path.Combine(directory, "processed.png");

		await WriteAtomicAsync(filePath, processedBytes, ct);

		// Return relative path (receiptId/filename) instead of absolute filesystem path
		return Path.Combine(receiptId.ToString(), "processed.png");
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

	public string GetImagePath(Guid receiptId, string fileName)
	{
		return Path.Combine(StorageRoot, receiptId.ToString(), fileName);
	}

	public Task DeleteReceiptImagesAsync(Guid receiptId, CancellationToken ct)
	{
		string directory = Path.Combine(StorageRoot, receiptId.ToString());
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
		return Task.CompletedTask;
	}
}
