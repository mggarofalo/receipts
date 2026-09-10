using Application.Models.Images;

namespace Application.Interfaces.Services;

public interface IImageStorageService
{
	Task<ReceiptImageSet> SaveImageSetAsync(
		Guid receiptId,
		byte[] originalBytes,
		string originalExtension,
		byte[] processedBytes,
		CancellationToken ct);
	string GetImagePath(Guid receiptId, string fileName);
	Task DeleteReceiptImagesAsync(Guid receiptId, CancellationToken ct);
	Task<ImageCleanupResult> CleanupUnreferencedAsync(
		IReadOnlySet<string> referencedPaths,
		DateTimeOffset createdBefore,
		CancellationToken ct);
}
