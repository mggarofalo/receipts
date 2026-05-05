using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Application.Interfaces.Services;
using Application.Models.Ocr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace Infrastructure.Services;

public sealed partial class OllamaReceiptExtractionService : IReceiptExtractionService
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		NumberHandling = JsonNumberHandling.AllowReadingFromString,
	};

	private readonly HttpClient _httpClient;
	private readonly VlmOcrOptions _options;
	private readonly ILogger<OllamaReceiptExtractionService> _logger;

	/// <summary>
	/// Maximum number of characters from the raw VLM response copied into exception messages.
	/// The raw body contains receipt PII (store, items, payment method, last-four card digits)
	/// and tends to propagate through telemetry channels — exception messages are not the right
	/// place for that. The full body is still available via the gated raw-response debug log
	/// when <see cref="VlmOcrOptions.LogRawResponses"/> is enabled. See RECEIPTS-639.
	/// </summary>
	internal const int ExceptionMessageMaxChars = 500;

	/// <summary>
	/// When the VLM's extracted <c>subtotal</c> disagrees with the sum of <c>items[].totalPrice</c>
	/// by more than this amount, the subtotal's confidence is downgraded so the wizard renders a
	/// review badge. The discrepancy is otherwise preserved as-extracted so the user can see both
	/// values and adjudicate. See RECEIPTS-663.
	/// </summary>
	internal const decimal SubtotalReconciliationTolerance = 0.05m;

	private static readonly string[] DateFormats =
	[
		"yyyy-MM-dd",
		"yyyy/MM/dd",
		"MM/dd/yyyy",
		"M/d/yyyy",
		"MM/dd/yy",
		"M/d/yy",
		"dd/MM/yyyy",
		"d/M/yyyy",
		"dd-MM-yyyy",
		"dd.MM.yyyy",
	];

	/// <summary>
	/// A valid card last-four is exactly four ASCII digits. Anything else (longer auth-code
	/// runs the VLM hallucinated, masked sequences like <c>****3409</c>, or empty strings) is
	/// rejected post-extraction so the downstream UI never displays a wrong value with high
	/// confidence. See RECEIPTS-627.
	/// <para>
	/// Pattern uses <c>[0-9]</c> rather than <c>\d</c> because .NET's default regex engine
	/// expands <c>\d</c> to the full Unicode Decimal_Number category (Arabic-Indic, Devanagari,
	/// etc.) — we want strictly ASCII 0-9 to match the documented contract.
	/// </para>
	/// </summary>
	[GeneratedRegex(@"^[0-9]{4}$")]
	private static partial Regex LastFourRegex();

	public OllamaReceiptExtractionService(
		HttpClient httpClient,
		IOptions<VlmOcrOptions> options,
		ILogger<OllamaReceiptExtractionService> logger)
	{
		ArgumentNullException.ThrowIfNull(httpClient);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);
		_httpClient = httpClient;
		_options = options.Value;
		_logger = logger;
	}

	public async Task<ParsedReceipt> ExtractAsync(byte[] imageBytes, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(imageBytes);
		if (imageBytes.Length == 0)
		{
			throw new ArgumentException("Image bytes cannot be empty.", nameof(imageBytes));
		}

		// Reject oversized images before allocating a base64 buffer (~133% of the input). Ollama's
		// default request body limit is well below 50 MB+ camera dumps mobile clients can produce;
		// rejecting here gives a clear ArgumentException at the boundary instead of an opaque
		// HttpRequestException or RequestEntityTooLarge from the daemon. See RECEIPTS-640.
		if (imageBytes.Length > _options.MaxImageBytes)
		{
			throw new ArgumentException(
				$"Image is {imageBytes.Length} bytes, exceeding the configured maximum of {_options.MaxImageBytes} bytes.",
				nameof(imageBytes));
		}

		ReceiptExtractionPromptValue prompt = ReceiptExtractionPrompt.Current;

		// All logs in this request — including those raised from inner helpers and the resilience
		// pipeline — get the prompt version stamped on them so a regression can be traced back to
		// the prompt that produced it. See RECEIPTS-639.
		using IDisposable? promptScope = _logger.BeginScope(new Dictionary<string, object>
		{
			["VlmPromptVersion"] = prompt.Version,
			["VlmModel"] = _options.Model,
		});

		_logger.LogDebug(
			"Extracting receipt via Ollama VLM (model={Model}, promptVersion={PromptVersion}, bytes={Bytes})",
			_options.Model, prompt.Version, imageBytes.Length);

		string base64 = Convert.ToBase64String(imageBytes);
		OllamaGenerateRequest request = new(
			Model: _options.Model,
			Prompt: prompt.Text,
			Images: [base64]);

		// The per-attempt timeout is enforced by the resilience pipeline (Polly Timeout
		// strategy registered in InfrastructureService). Each retry receives a fresh
		// VlmOcrOptions.TimeoutSeconds budget — see RECEIPTS-630.
		string responseBody;
		try
		{
			using HttpResponseMessage httpResponse = await _httpClient.PostAsJsonAsync(
				"api/generate", request, JsonOptions, cancellationToken);

			httpResponse.EnsureSuccessStatusCode();

			responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
		}
		catch (TimeoutRejectedException)
		{
			throw new TimeoutException($"Ollama VLM call timed out after {_options.TimeoutSeconds}s.");
		}

		// Ollama's /api/generate returns either:
		//   1. A single JSON object (when stream=false honored by the server — what we ask for)
		//   2. NDJSON: one JSON object per line, each a partial chunk, the last with done=true
		//      (some Ollama versions stream image-input responses despite stream=false on the
		//      request — observed on glm-ocr with rasterized-PDF inputs)
		// We handle both transparently: parse line-by-line, concatenate the `response` chunks,
		// take the final object's `done` flag. A single-object body is just a one-line NDJSON.
		// See RECEIPTS-640 (the original done=false guard) and the hardening that motivated this.
		OllamaGenerateResponse? generateResponse = ParseOllamaResponse(responseBody);

		if (generateResponse is null || string.IsNullOrWhiteSpace(generateResponse.Response))
		{
			throw new InvalidOperationException("Ollama VLM returned an empty response.");
		}

		// After NDJSON consolidation, done=false means the stream was truncated mid-flight (the
		// connection closed before the model reached its done=true terminal chunk). The JSON in
		// `Response` will be partial and any downstream JsonException would mask the real cause.
		if (!generateResponse.Done)
		{
			throw new InvalidOperationException(
				"Ollama VLM response was truncated (no done=true chunk received); the upstream connection likely dropped mid-stream. Retry, or confirm the daemon is reachable for the full response.");
		}

		// The raw response carries PII (store, items, payment method, last-four). Logging is gated
		// behind VlmOcrOptions.LogRawResponses (default off in production) — see RECEIPTS-639.
		if (_options.LogRawResponses)
		{
			_logger.LogDebug("Ollama VLM raw response: {Response}", generateResponse.Response);
		}

		VlmReceiptPayload payload;
		try
		{
			payload = JsonSerializer.Deserialize<VlmReceiptPayload>(generateResponse.Response, JsonOptions)
				?? throw new InvalidOperationException("Ollama VLM produced a null receipt payload.");
		}
		catch (JsonException ex)
		{
			throw new InvalidOperationException(
				$"Failed to parse Ollama VLM response as JSON. Raw response (truncated): {Truncate(generateResponse.Response, ExceptionMessageMaxChars)}", ex);
		}

		if (payload.SchemaVersion != VlmReceiptPayload.CurrentSchemaVersion)
		{
			// Don't include the raw payload in this message — it is PII-bearing and exception
			// messages routinely propagate through telemetry. The version mismatch alone is
			// enough to identify the root cause; full bodies are available via the LogRawResponses
			// flag for local diagnostics.
			throw new InvalidOperationException(
				$"Ollama VLM payload schema_version mismatch (expected={VlmReceiptPayload.CurrentSchemaVersion}, actual={payload.SchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "null"}, promptVersion={prompt.Version}).");
		}

		return MapToParsedReceipt(payload);
	}

	/// <summary>
	/// Truncates <paramref name="value"/> to at most <paramref name="maxChars"/> UTF-16 code
	/// units, appending an ellipsis suffix when the string is cut. Used to keep exception
	/// messages from leaking the entire VLM payload (PII) into telemetry. See RECEIPTS-639.
	/// <para>
	/// If the cut boundary lands between the two halves of a UTF-16 surrogate pair, the high
	/// surrogate is dropped rather than orphaned. An unpaired high surrogate would produce an
	/// ill-formed string that <see cref="System.Text.Json"/> (the default formatter for many
	/// structured log sinks) rejects with an <see cref="InvalidOperationException"/> in strict
	/// mode, masking the original exception in telemetry.
	/// </para>
	/// </summary>
	internal static string Truncate(string value, int maxChars)
	{
		if (value.Length <= maxChars)
		{
			return value;
		}

		int safeMax = maxChars > 0 && char.IsHighSurrogate(value[maxChars - 1])
			? maxChars - 1
			: maxChars;

		return string.Concat(value.AsSpan(0, safeMax), "... [truncated]");
	}

	/// <summary>
	/// Parses an Ollama <c>/api/generate</c> response body, transparently handling both the
	/// single-JSON-object form (returned when <c>stream=false</c> is honored) and the NDJSON
	/// streaming form (one JSON object per line, the last carrying <c>done=true</c>).
	/// Concatenates the per-chunk <c>response</c> strings and returns a synthetic
	/// <see cref="OllamaGenerateResponse"/> carrying the joined text plus the final
	/// <c>done</c>/<c>model</c> values. Returns <c>null</c> when the body is empty or only
	/// whitespace.
	/// </summary>
	internal static OllamaGenerateResponse? ParseOllamaResponse(string body)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			return null;
		}

		// Single-line JSON-object body: parse directly. This is the happy path on Ollama
		// builds that respect stream=false.
		string trimmed = body.TrimStart();
		bool looksLikeNdjson = trimmed.IndexOf('\n') >= 0;
		if (!looksLikeNdjson)
		{
			return JsonSerializer.Deserialize<OllamaGenerateResponse>(body, JsonOptions);
		}

		// NDJSON: parse each non-blank line as an OllamaGenerateResponse, concatenate the
		// `response` chunks, take the last object's metadata. The final chunk in a healthy
		// stream has done=true and an empty response; truncated streams have all done=false.
		System.Text.StringBuilder accumulator = new();
		string? lastModel = null;
		bool lastDone = false;
		foreach (string line in body.Split('\n'))
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			OllamaGenerateResponse? chunk = JsonSerializer.Deserialize<OllamaGenerateResponse>(line, JsonOptions);
			if (chunk is null)
			{
				continue;
			}

			if (!string.IsNullOrEmpty(chunk.Response))
			{
				accumulator.Append(chunk.Response);
			}
			lastModel = chunk.Model;
			lastDone = chunk.Done;
		}

		return new OllamaGenerateResponse(lastModel ?? string.Empty, accumulator.ToString(), lastDone);
	}

	internal static ParsedReceipt MapToParsedReceipt(VlmReceiptPayload payload)
	{
		FieldConfidence<string> storeName = !string.IsNullOrWhiteSpace(payload.Store?.Name)
			? FieldConfidence<string>.High(payload.Store.Name)
			: FieldConfidence<string>.None();

		FieldConfidence<string?> storeAddress = !string.IsNullOrWhiteSpace(payload.Store?.Address)
			? FieldConfidence<string?>.High(payload.Store.Address)
			: FieldConfidence<string?>.None();

		FieldConfidence<string?> storePhone = !string.IsNullOrWhiteSpace(payload.Store?.Phone)
			? FieldConfidence<string?>.High(payload.Store.Phone)
			: FieldConfidence<string?>.None();

		FieldConfidence<DateOnly> date = TryParseDate(payload.Datetime) is { } d
			? FieldConfidence<DateOnly>.High(d)
			: FieldConfidence<DateOnly>.None();

		List<ParsedReceiptItem> items = MergeWeightSublines(payload.Items ?? []).Select(MapItem).ToList();

		FieldConfidence<decimal> subtotal = ReconcileSubtotal(payload.Subtotal, items);

		List<ParsedTaxLine> taxLines = (payload.TaxLines ?? []).Select(MapTaxLine).ToList();

		FieldConfidence<decimal> total = payload.Total is { } t
			? FieldConfidence<decimal>.High(t)
			: FieldConfidence<decimal>.None();

		// Preserve every payment tender from the VLM payload. The legacy PaymentMethod field
		// is kept populated with the first non-empty method string for backward compatibility;
		// new consumers should read the Payments list instead.
		List<ParsedPayment> payments = (payload.Payments ?? []).Select(MapPayment).ToList();

		string? primaryMethod = payload.Payments?
			.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Method))?.Method;
		FieldConfidence<string?> paymentMethod = !string.IsNullOrWhiteSpace(primaryMethod)
			? FieldConfidence<string?>.High(primaryMethod)
			: FieldConfidence<string?>.None();

		FieldConfidence<string?> receiptId = !string.IsNullOrWhiteSpace(payload.ReceiptId)
			? FieldConfidence<string?>.High(payload.ReceiptId)
			: FieldConfidence<string?>.None();

		FieldConfidence<string?> storeNumber = !string.IsNullOrWhiteSpace(payload.StoreNumber)
			? FieldConfidence<string?>.High(payload.StoreNumber)
			: FieldConfidence<string?>.None();

		FieldConfidence<string?> terminalId = !string.IsNullOrWhiteSpace(payload.TerminalId)
			? FieldConfidence<string?>.High(payload.TerminalId)
			: FieldConfidence<string?>.None();

		return new ParsedReceipt(storeName, date, items, subtotal, taxLines, total, paymentMethod)
		{
			StoreAddress = storeAddress,
			StorePhone = storePhone,
			Payments = payments,
			ReceiptId = receiptId,
			StoreNumber = storeNumber,
			TerminalId = terminalId,
		};
	}

	/// <summary>
	/// Merges weighted-item sub-lines into their parent item. VLMs (both qwen2.5vl:3b and :7b)
	/// emit "2.460 lb. @ 1 lb. /0.50" as its own item row when prompted via few-shot examples
	/// (they reliably extract the quantity/unitPrice but not the parent-merging structure).
	/// We detect these sub-lines deterministically: null <c>code</c> + description containing
	/// <c>" @ "</c> + non-null <c>quantity</c> and <c>unitPrice</c>. Two parent shapes merge:
	/// (a) parent shares the sub-line's <c>lineTotal</c> with null <c>quantity</c> — absorb
	/// quantity/unitPrice into the parent; (b) phantom-header parent (lineTotal/qty/unitPrice
	/// all 0 or null) — Walmart prints the price on the weight line, so absorb the sub-line
	/// entirely (RECEIPTS-662). The sub-line's taxCode wins for case (b) because the phantom
	/// row's marker (often "F") is the wrong code for taxable produce-by-weight.
	/// </summary>
	internal static List<VlmReceiptItem> MergeWeightSublines(List<VlmReceiptItem> items)
	{
		List<VlmReceiptItem> merged = [];
		foreach (VlmReceiptItem item in items)
		{
			if (IsWeightSubline(item) && merged.Count > 0)
			{
				VlmReceiptItem parent = merged[^1];
				if (!IsPhantomParent(parent) && parent.LineTotal == item.LineTotal && parent.Quantity is null)
				{
					parent.Quantity = item.Quantity;
					parent.UnitPrice = item.UnitPrice;
					// If the V3 prompt echoes taxCode on the sub-line but not on the parent,
					// absorb it so the merged item doesn't drop the code entirely. The parent's
					// taxCode wins when both are present (the printed marker sits next to the
					// parent line on the physical receipt).
					if (string.IsNullOrWhiteSpace(parent.TaxCode) && !string.IsNullOrWhiteSpace(item.TaxCode))
					{
						parent.TaxCode = item.TaxCode;
					}
					continue;
				}

				if (IsPhantomParent(parent) && item.LineTotal is > 0)
				{
					parent.LineTotal = item.LineTotal;
					parent.Quantity = item.Quantity;
					parent.UnitPrice = item.UnitPrice;
					if (!string.IsNullOrWhiteSpace(item.TaxCode))
					{
						parent.TaxCode = item.TaxCode;
					}
					continue;
				}
			}
			merged.Add(item);
		}
		return merged;
	}

	private static bool IsWeightSubline(VlmReceiptItem item)
	{
		return string.IsNullOrWhiteSpace(item.Code)
			&& !string.IsNullOrWhiteSpace(item.Description)
			&& item.Description.Contains(" @ ", StringComparison.Ordinal)
			&& item.Quantity is not null
			&& item.UnitPrice is not null;
	}

	private static bool IsPhantomParent(VlmReceiptItem item)
	{
		return item.LineTotal is null or 0m
			&& item.Quantity is null or 0m
			&& item.UnitPrice is null or 0m;
	}

	/// <summary>
	/// Cross-checks the VLM's extracted <c>subtotal</c> against the sum of item line totals.
	/// When the two disagree by more than <see cref="SubtotalReconciliationTolerance"/>, the
	/// extracted value is preserved (so the user sees what the VLM read) but the confidence is
	/// downgraded to <see cref="ConfidenceLevel.Low"/> so the wizard surfaces a review badge.
	/// Without this cross-check the wizard's on-page subtotal and line-item sum could disagree
	/// silently and break the user's mental balance check. See RECEIPTS-663.
	/// </summary>
	internal static FieldConfidence<decimal> ReconcileSubtotal(decimal? extracted, List<ParsedReceiptItem> items)
	{
		if (extracted is not { } subtotal)
		{
			return FieldConfidence<decimal>.None();
		}

		decimal itemSum = items
			.Where(i => i.TotalPrice.IsPresent)
			.Sum(i => i.TotalPrice.Value);
		decimal delta = Math.Abs(subtotal - itemSum);
		return delta <= SubtotalReconciliationTolerance
			? FieldConfidence<decimal>.High(subtotal)
			: FieldConfidence<decimal>.Low(subtotal);
	}

	private static ParsedReceiptItem MapItem(VlmReceiptItem item)
	{
		FieldConfidence<string?> code = !string.IsNullOrWhiteSpace(item.Code)
			? FieldConfidence<string?>.High(item.Code)
			: FieldConfidence<string?>.None();

		FieldConfidence<string> description = !string.IsNullOrWhiteSpace(item.Description)
			? FieldConfidence<string>.High(item.Description)
			: FieldConfidence<string>.None();

		FieldConfidence<decimal> quantity = item.Quantity is { } q
			? FieldConfidence<decimal>.High(q)
			: FieldConfidence<decimal>.None();

		FieldConfidence<decimal> unitPrice = item.UnitPrice is { } u
			? FieldConfidence<decimal>.High(u)
			: FieldConfidence<decimal>.None();

		FieldConfidence<decimal> totalPrice = item.LineTotal is { } t
			? FieldConfidence<decimal>.High(t)
			: FieldConfidence<decimal>.None();

		FieldConfidence<string?> taxCode = !string.IsNullOrWhiteSpace(item.TaxCode)
			? FieldConfidence<string?>.High(item.TaxCode)
			: FieldConfidence<string?>.None();

		return new ParsedReceiptItem(code, description, quantity, unitPrice, totalPrice)
		{
			TaxCode = taxCode,
		};
	}

	private static ParsedPayment MapPayment(VlmPayment payment)
	{
		FieldConfidence<string?> method = !string.IsNullOrWhiteSpace(payment.Method)
			? FieldConfidence<string?>.High(payment.Method)
			: FieldConfidence<string?>.None();

		FieldConfidence<decimal?> amount = payment.Amount is { } a
			? FieldConfidence<decimal?>.High(a)
			: FieldConfidence<decimal?>.None();

		FieldConfidence<string?> lastFour = ValidateLastFour(payment.LastFour);

		return new ParsedPayment(method, amount, lastFour);
	}

	/// <summary>
	/// Reject any <c>lastFour</c> value that is not exactly four ASCII digits. qwen2.5vl:3b
	/// has been observed to hallucinate longer digit runs (e.g. lifting the APPR# reference
	/// number from elsewhere on the receipt) when the printed card-tail is illegible. Setting
	/// the value to <c>null</c> with <c>Low</c> confidence is safer than surfacing a wrong
	/// value at <c>High</c> confidence — the UI already prompts the user to confirm low-confidence
	/// fields, so we degrade gracefully rather than corrupt downstream records.
	/// </summary>
	internal static FieldConfidence<string?> ValidateLastFour(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return FieldConfidence<string?>.None();
		}

		string trimmed = raw.Trim();
		return LastFourRegex().IsMatch(trimmed)
			? FieldConfidence<string?>.High(trimmed)
			: FieldConfidence<string?>.Low(null);
	}

	private static DateOnly? TryParseDate(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return null;
		}

		string trimmed = raw.Trim();

		// VLM may return date + time separated by 'T' (ISO-8601) or ' '
		// (e.g. "2026-01-14T17:57:20" or "01/14/26 17:57:20"). Keep the date part only.
		int splitIndex = trimmed.IndexOfAny(['T', ' ']);
		if (splitIndex > 0)
		{
			trimmed = trimmed[..splitIndex];
		}

		if (DateOnly.TryParseExact(
			trimmed, DateFormats, CultureInfo.InvariantCulture,
			DateTimeStyles.None, out DateOnly exact))
		{
			return exact;
		}

		return DateOnly.TryParse(
			trimmed, CultureInfo.InvariantCulture,
			DateTimeStyles.None, out DateOnly invariant)
			? invariant
			: null;
	}

	private static ParsedTaxLine MapTaxLine(VlmTaxLine tax)
	{
		FieldConfidence<string> label = !string.IsNullOrWhiteSpace(tax.Label)
			? FieldConfidence<string>.High(tax.Label)
			: FieldConfidence<string>.None();

		FieldConfidence<decimal> amount = tax.Amount is { } a
			? FieldConfidence<decimal>.High(a)
			: FieldConfidence<decimal>.None();

		return new ParsedTaxLine(label, amount);
	}
}
