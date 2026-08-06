using API.Generated.Dtos;
using Application.Commands.Receipt.UploadImage;
using Asp.Versioning;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers.Core;

[ApiVersion("1.0")]
[ApiController]
[Route("api/receipts")]
[Authorize]
public class ReceiptImageController(
	IMediator mediator,
	ILogger<ReceiptImageController> logger) : ControllerBase
{
	private const long MaxFileSizeBytes = 20 * 1024 * 1024; // 20 MB

	private static readonly HashSet<string> AllowedContentTypes =
	[
		"image/jpeg",
		"image/png",
	];

	[HttpPost("{receiptId}/image")]
	[RequestSizeLimit(20 * 1024 * 1024)]
	[EndpointSummary("Upload an image for a receipt")]
	[EndpointDescription("Accepts a JPEG or PNG image, saves the original, runs preprocessing (grayscale, adaptive threshold, deskew), and returns both image paths.")]
	public async Task<Results<Ok<UploadReceiptImageResponse>, NotFound, BadRequest<ProblemDetails>, StatusCodeHttpResult>> UploadImage(
		[FromRoute] Guid receiptId,
		IFormFile? file,
		CancellationToken cancellationToken = default)
	{
		if (file is null || file.Length == 0)
		{
			return ApiProblem.BadRequest("No file was uploaded.");
		}

		if (file.Length > MaxFileSizeBytes)
		{
			return ApiProblem.BadRequest($"File size exceeds the maximum allowed size of {MaxFileSizeBytes / (1024 * 1024)} MB.");
		}

		if (!AllowedContentTypes.Contains(file.ContentType))
		{
			return TypedResults.StatusCode(StatusCodes.Status415UnsupportedMediaType);
		}

		string extension = Path.GetExtension(file.FileName)?.ToLowerInvariant() ?? string.Empty;
		if (string.IsNullOrEmpty(extension))
		{
			extension = file.ContentType == "image/png" ? ".png" : ".jpg";
		}

		byte[] imageBytes;
		using (MemoryStream ms = new())
		{
			await file.CopyToAsync(ms, cancellationToken);
			imageBytes = ms.ToArray();
		}

		UploadReceiptImageCommand command;
		try
		{
			command = new UploadReceiptImageCommand(receiptId, imageBytes, file.ContentType, extension);
		}
		catch (ArgumentException ex)
		{
			return ApiProblem.BadRequest(ex.Message);
		}

		UploadReceiptImageResult result;
		try
		{
			result = await mediator.Send(command, cancellationToken);
		}
		catch (KeyNotFoundException)
		{
			logger.LogWarning("Receipt {Id} not found for image upload", receiptId);
			return TypedResults.NotFound();
		}
		catch (InvalidOperationException ex)
		{
			logger.LogWarning(ex, "Invalid image uploaded for receipt {Id}", receiptId);
			return ApiProblem.BadRequest(ex.Message);
		}

		return TypedResults.Ok(new UploadReceiptImageResponse
		{
			OriginalImagePath = result.OriginalImagePath,
			ProcessedImagePath = result.ProcessedImagePath,
		});
	}
}
