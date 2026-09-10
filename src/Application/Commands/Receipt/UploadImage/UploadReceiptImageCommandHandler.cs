using Application.Interfaces.Services;
using Application.Models.Images;
using Mediator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Application.Commands.Receipt.UploadImage;

public class UploadReceiptImageCommandHandler(
	IReceiptService receiptService,
	IImageStorageService imageStorageService,
	IImageProcessingService imageProcessingService,
	IReceiptImageReconciliationLock reconciliationLock,
	ILogger<UploadReceiptImageCommandHandler> logger) : IRequestHandler<UploadReceiptImageCommand, UploadReceiptImageResult>
{
	public UploadReceiptImageCommandHandler(
		IReceiptService receiptService,
		IImageStorageService imageStorageService,
		IImageProcessingService imageProcessingService,
		ILogger<UploadReceiptImageCommandHandler> logger)
		: this(receiptService, imageStorageService, imageProcessingService, NoopReconciliationLock.Instance, logger)
	{
	}

	public UploadReceiptImageCommandHandler(
		IReceiptService receiptService,
		IImageStorageService imageStorageService,
		IImageProcessingService imageProcessingService)
		: this(receiptService, imageStorageService, imageProcessingService, NoopReconciliationLock.Instance, NullLogger<UploadReceiptImageCommandHandler>.Instance)
	{
	}

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
		await using IAsyncDisposable reconciliationLease =
			await reconciliationLock.AcquireAsync(cancellationToken);

		// Publish both variants as one immutable directory version. The database switches its
		// pair of references only after that directory is complete, so a processed-file or DB
		// failure cannot expose a mixed set or damage the prior version.
		ReceiptImageSet published = await imageStorageService.SaveImageSetAsync(
			request.ReceiptId,
			request.ImageBytes,
			request.FileExtension,
			processed.ProcessedBytes,
			cancellationToken);

		try
		{
			await receiptService.ReplaceImagePathsAsync(
				request.ReceiptId, published, cancellationToken);
		}
		catch (Exception ex)
		{
			// The immutable set is complete but not referenced. Do not guess whether the DB
			// commit happened; the grace-period sweep reconciles storage from durable paths.
			logger.LogWarning(ex, "Published an unreferenced image set for receipt {ReceiptId}; cleanup will retry", request.ReceiptId);
			throw;
		}

		// Deliberately defer deletion of the previous version to the reconciler. A backup
		// import can legitimately restore an older path; deleting here after the database
		// transaction commits would race that import and could remove its newly-current set.

		return new UploadReceiptImageResult(published.OriginalImagePath, published.ProcessedImagePath);
	}

	private sealed class NoopReconciliationLock : IReceiptImageReconciliationLock
	{
		public static readonly NoopReconciliationLock Instance = new();

		public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
			=> Task.FromResult<IAsyncDisposable>(NoopLease.Instance);
	}

	private sealed class NoopLease : IAsyncDisposable
	{
		public static readonly NoopLease Instance = new();

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
