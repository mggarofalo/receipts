using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Receipt.UploadImage;

public class UploadReceiptImageCommandHandler(
	IReceiptService receiptService,
	IImageStorageService imageStorageService,
	IImageProcessingService imageProcessingService) : IRequestHandler<UploadReceiptImageCommand, UploadReceiptImageResult>
{
	public async ValueTask<UploadReceiptImageResult> Handle(UploadReceiptImageCommand request, CancellationToken cancellationToken)
	{
		bool exists = await receiptService.ExistsAsync(request.ReceiptId, cancellationToken);
		if (!exists)
		{
			throw new KeyNotFoundException($"Receipt {request.ReceiptId} not found.");
		}

		// Validate and preprocess the uploaded bytes IN MEMORY before writing anything to the
		// receipt's permanent storage. PreprocessAsync performs the magic-byte/format and
		// dimension checks, so a rejected upload throws here having touched nothing on disk.
		//
		// The previous ordering wrote the new original to the receipt's permanent location first
		// and, on any failure, recursively deleted ALL of the receipt's images. That meant a user
		// re-uploading an invalid file destroyed the previously-good original/processed images.
		// By validating first and only persisting after success, the existing images are always
		// preserved when a new upload is rejected.
		ImageProcessingResult processed = await imageProcessingService.PreprocessAsync(
			request.ImageBytes, request.ContentType, cancellationToken);

		// Validation passed — only now does anything reach disk. Both files are written from
		// validated bytes, so there is no partially-written invalid state to clean up, and the
		// receipt's pre-existing images are never deleted on a rejected upload.
		string originalPath = await imageStorageService.SaveOriginalAsync(
			request.ReceiptId, request.ImageBytes, request.FileExtension, cancellationToken);

		string processedPath = await imageStorageService.SaveProcessedAsync(
			request.ReceiptId, processed.ProcessedBytes, cancellationToken);

		await receiptService.UpdateImagePathsAsync(
			request.ReceiptId, originalPath, processedPath, cancellationToken);

		return new UploadReceiptImageResult(originalPath, processedPath);
	}
}
