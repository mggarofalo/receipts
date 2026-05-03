using API.Generated.Dtos;
using Application.Commands.Receipt.Scan;
using Application.Exceptions;
using Application.Models.Ocr;
using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using DtoConfidenceLevel = API.Generated.Dtos.ConfidenceLevel;
using OcrConfidenceLevel = Application.Models.Ocr.ConfidenceLevel;

namespace API.Controllers.Core;

[ApiVersion("1.0")]
[ApiController]
[Route("api/receipts")]
[Authorize]
public class ReceiptScanController(
	IMediator mediator,
	ILogger<ReceiptScanController> logger) : ControllerBase
{
	private const long MaxFileSizeBytes = 20 * 1024 * 1024; // 20 MB

	private static readonly HashSet<string> AllowedContentTypes =
	[
		"image/jpeg",
		"image/png",
		"application/pdf",
	];

	[HttpPost("scan")]
	[RequestSizeLimit(20 * 1024 * 1024)]
	[EndpointSummary("Scan a receipt image or PDF and return a proposed receipt")]
	[EndpointDescription("Accepts a JPEG or PNG image, or a PDF document. Images are sent directly to the receipt extraction service (a local vision-language model). PDFs have their first page rasterized to a PNG at 200 DPI; the rendered image is then sent to the VLM. Additional pages are silently ignored — when a PDF has more than one page, the response carries droppedPageCount set to the number of skipped pages so clients can warn the user that the proposal represents only the first page. Returns a proposed receipt with per-field confidence scores. The proposal is NOT persisted.")]
	[ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
	public async Task<Results<Ok<ProposedReceiptResponse>, BadRequest<string>, StatusCodeHttpResult, UnprocessableEntity<string>>> ScanReceipt(
		IFormFile? file,
		CancellationToken cancellationToken = default)
	{
		if (file is null || file.Length == 0)
		{
			return TypedResults.BadRequest("No file was uploaded.");
		}

		if (file.Length > MaxFileSizeBytes)
		{
			return TypedResults.BadRequest($"File size exceeds the maximum allowed size of {MaxFileSizeBytes / (1024 * 1024)} MB.");
		}

		if (!AllowedContentTypes.Contains(file.ContentType))
		{
			return TypedResults.StatusCode(StatusCodes.Status415UnsupportedMediaType);
		}

		byte[] imageBytes;
		using (MemoryStream ms = new())
		{
			await file.CopyToAsync(ms, cancellationToken);
			imageBytes = ms.ToArray();
		}

		ScanReceiptCommand command;
		try
		{
			command = new ScanReceiptCommand(imageBytes, file.ContentType);
		}
		catch (ArgumentException ex)
		{
			return TypedResults.BadRequest(ex.Message);
		}

		ScanReceiptResult result;
		try
		{
			result = await mediator.Send(command, cancellationToken);
		}
		catch (OcrNoTextException ex)
		{
			logger.LogWarning(ex, "OCR returned no text for scanned receipt image");
			return TypedResults.UnprocessableEntity("The image could not be read or OCR returned no text.");
		}
		catch (InvalidOperationException ex)
		{
			logger.LogWarning(ex, "Failed to process scanned receipt image");
			return TypedResults.UnprocessableEntity(ex.Message);
		}

		return TypedResults.Ok(MapToResponse(result));
	}

	private static ProposedReceiptResponse MapToResponse(ScanReceiptResult result)
	{
		ParsedReceipt parsed = result.ParsedReceipt;

		return new ProposedReceiptResponse
		{
			StoreName = parsed.StoreName.Value,
			StoreNameConfidence = MapConfidence(parsed.StoreName.Confidence),
			StoreAddress = parsed.StoreAddress.Value,
			StoreAddressConfidence = MapConfidence(parsed.StoreAddress.Confidence),
			StorePhone = parsed.StorePhone.Value,
			StorePhoneConfidence = MapConfidence(parsed.StorePhone.Confidence),
			Date = parsed.Date.Value,
			DateConfidence = MapConfidence(parsed.Date.Confidence),
			Items = parsed.Items.Select(MapItem).ToList(),
			Subtotal = ToNullableDouble(parsed.Subtotal.Value),
			SubtotalConfidence = MapConfidence(parsed.Subtotal.Confidence),
			TaxLines = parsed.TaxLines.Select(MapTaxLine).ToList(),
			Total = ToNullableDouble(parsed.Total.Value),
			TotalConfidence = MapConfidence(parsed.Total.Confidence),
			PaymentMethod = parsed.PaymentMethod.Value,
			PaymentMethodConfidence = MapConfidence(parsed.PaymentMethod.Confidence),
#pragma warning disable CS0612 // Payments is intentionally populated for back-compat with the existing client UI; RECEIPTS-658 removes both the field and this writer.
			Payments = parsed.Payments.Select(MapPayment).ToList(),
#pragma warning restore CS0612
			ProposedTransactions = (result.ProposedTransactions ?? [])
				.Select(MapProposedTransaction).ToList(),
			ReceiptId = parsed.ReceiptId.Value,
			ReceiptIdConfidence = MapConfidence(parsed.ReceiptId.Confidence),
			StoreNumber = parsed.StoreNumber.Value,
			StoreNumberConfidence = MapConfidence(parsed.StoreNumber.Confidence),
			TerminalId = parsed.TerminalId.Value,
			TerminalIdConfidence = MapConfidence(parsed.TerminalId.Confidence),
			DroppedPageCount = result.DroppedPageCount,
		};
	}

	private static ProposedReceiptItemResponse MapItem(ParsedReceiptItem item)
	{
		// RECEIPTS-661: Weight-priced items (e.g. "TOMATO 2.300 lb @ 0.92") arrive
		// from the VLM with quantity + unitPrice populated and a recognised
		// confidence, but no separate totalPrice — the model didn't print one as
		// a distinct line, so the value-type defaults to 0 and confidence to
		// None. Without intervention the wizard renders such rows as $0.00 and
		// the rolling subtotal is wrong by the same amount. Derive the missing
		// total here so the response carries a usable value (single source of
		// truth) rather than relying on every client to recompute it.
		(decimal? derivedTotal, OcrConfidenceLevel derivedConfidence) = DeriveTotalPrice(item);

		return new ProposedReceiptItemResponse
		{
			Code = item.Code.Value,
			CodeConfidence = MapConfidence(item.Code.Confidence),
			Description = item.Description.Value,
			DescriptionConfidence = MapConfidence(item.Description.Confidence),
			Quantity = ToNullableDouble(item.Quantity.Value),
			QuantityConfidence = MapConfidence(item.Quantity.Confidence),
			UnitPrice = ToNullableDouble(item.UnitPrice.Value),
			UnitPriceConfidence = MapConfidence(item.UnitPrice.Confidence),
			TotalPrice = ToNullableDouble(derivedTotal),
			TotalPriceConfidence = MapConfidence(derivedConfidence),
			TaxCode = item.TaxCode.Value,
			TaxCodeConfidence = MapConfidence(item.TaxCode.Confidence),
		};
	}

	/// <summary>
	/// RECEIPTS-661: When the VLM omits a separate <c>totalPrice</c> for a
	/// weight-priced or per-unit-priced item but supplies both <c>quantity</c>
	/// and <c>unitPrice</c>, derive the total here. The derived confidence is
	/// the lower of the two source confidences (both are non-None by precondition),
	/// so high+high => high, high+medium => medium, medium+low => low. When the
	/// VLM already supplied <c>totalPrice</c> at any confidence (Low/Medium/High)
	/// the original value is passed through unchanged.
	/// </summary>
	private static (decimal? TotalPrice, OcrConfidenceLevel Confidence) DeriveTotalPrice(ParsedReceiptItem item)
	{
		bool totalPriceMissing = item.TotalPrice.Confidence == OcrConfidenceLevel.None;
		bool quantityPresent = item.Quantity.Confidence != OcrConfidenceLevel.None;
		bool unitPricePresent = item.UnitPrice.Confidence != OcrConfidenceLevel.None;

		if (totalPriceMissing && quantityPresent && unitPricePresent)
		{
			// Decimal multiplication preserves the precision of the source
			// receipt values (e.g. 2.300 * 0.92 = 2.116) without IEEE-754 noise.
			decimal computed = item.Quantity.Value * item.UnitPrice.Value;
			OcrConfidenceLevel derived = item.Quantity.Confidence < item.UnitPrice.Confidence
				? item.Quantity.Confidence
				: item.UnitPrice.Confidence;
			return (computed, derived);
		}

		return (item.TotalPrice.Value, item.TotalPrice.Confidence);
	}

	private static ProposedTaxLineResponse MapTaxLine(ParsedTaxLine taxLine)
	{
		return new ProposedTaxLineResponse
		{
			Label = taxLine.Label.Value,
			LabelConfidence = MapConfidence(taxLine.Label.Confidence),
			Amount = ToNullableDouble(taxLine.Amount.Value),
			AmountConfidence = MapConfidence(taxLine.Amount.Confidence),
		};
	}

	private static ProposedPaymentResponse MapPayment(ParsedPayment payment)
	{
		return new ProposedPaymentResponse
		{
			Method = payment.Method.Value,
			MethodConfidence = MapConfidence(payment.Method.Confidence),
			Amount = ToNullableDouble(payment.Amount.Value),
			AmountConfidence = MapConfidence(payment.Amount.Confidence),
			LastFour = payment.LastFour.Value,
			LastFourConfidence = MapConfidence(payment.LastFour.Confidence),
		};
	}

	private static ProposedTransactionResponse MapProposedTransaction(ProposedTransaction txn)
	{
		return new ProposedTransactionResponse
		{
			CardId = txn.CardId.Value,
			CardIdConfidence = MapConfidence(txn.CardId.Confidence),
			AccountId = txn.AccountId.Value,
			AccountIdConfidence = MapConfidence(txn.AccountId.Confidence),
			Amount = ToNullableDouble(txn.Amount.Value),
			AmountConfidence = MapConfidence(txn.Amount.Confidence),
			Date = txn.Date.Value,
			DateConfidence = MapConfidence(txn.Date.Confidence),
			MethodSnapshot = txn.MethodSnapshot.Value,
		};
	}

	private static double? ToNullableDouble(decimal? value) =>
		value.HasValue ? (double)value.Value : null;

	private static DtoConfidenceLevel MapConfidence(OcrConfidenceLevel level)
	{
		return level switch
		{
			OcrConfidenceLevel.None => DtoConfidenceLevel.None,
			OcrConfidenceLevel.Low => DtoConfidenceLevel.Low,
			OcrConfidenceLevel.Medium => DtoConfidenceLevel.Medium,
			OcrConfidenceLevel.High => DtoConfidenceLevel.High,
			_ => DtoConfidenceLevel.None,
		};
	}
}
