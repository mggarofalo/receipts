using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.Interfaces.Services;
using Application.Models.Ocr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Timeout;
using SkiaSharp;

namespace Infrastructure.Services;

/// <summary>
/// Anthropic-backed implementation of <see cref="IReceiptExtractionService"/> (RECEIPTS-652).
/// Posts a base64 image plus the canonical <see cref="ReceiptExtractionPrompt"/> to the
/// Anthropic Messages API and forces structured JSON output via tool-use — the model is
/// pinned to call a single tool whose input schema mirrors <see cref="VlmReceiptPayload"/>,
/// so the response never contains JSON-in-text that needs lenient parsing.
/// <para>
/// The service mirrors the resilience and observability shape of
/// <see cref="OllamaReceiptExtractionService"/>: schema_version validation, prompt-version
/// log scopes, PII-gated raw-response logging, max-image-bytes guard, and exception-message
/// truncation. The mapper from <see cref="VlmReceiptPayload"/> to
/// <see cref="ParsedReceipt"/> is shared via <see cref="OllamaReceiptExtractionService.MapToParsedReceipt"/>
/// so the two providers produce identical domain output for the same payload.
/// </para>
/// </summary>
public sealed class AnthropicReceiptExtractionService : IReceiptExtractionService
{
	internal const string SubmitToolName = "submit_receipt";

	/// <summary>
	/// Maximum number of characters from the raw VLM response copied into exception
	/// messages. The raw body contains receipt PII so exception messages — which
	/// routinely propagate through telemetry — must not leak it. Mirrors the Ollama
	/// service's contract (RECEIPTS-639).
	/// </summary>
	internal const int ExceptionMessageMaxChars = 500;

	/// <summary>
	/// Maximum number of downscale passes before declaring the image cannot be made to
	/// fit under <see cref="AnthropicOptions.MaxRawImageBytes"/> (RECEIPTS-654). Each pass
	/// shrinks linear dimensions by <c>sqrt(target / current) * safetyMargin</c>, so three
	/// passes can reduce a 7-8 MB rasterized PNG down to well under the cap; pathological
	/// inputs (e.g. high-entropy noise that doesn't compress) eventually surface as a
	/// clear <see cref="InvalidOperationException"/> instead of looping forever.
	/// </summary>
	internal const int MaxDownscaleAttempts = 3;

	/// <summary>
	/// Linear-scale safety multiplier applied to the analytical scale factor on each
	/// downscale pass (RECEIPTS-654). PNG byte size scales sub-quadratically with linear
	/// dimensions for natural images (compression efficiency improves at lower
	/// resolution), so an exact <c>sqrt(target / current)</c> factor often overshoots and
	/// produces an output still slightly above the cap. The 0.95 cushion keeps the first
	/// pass under the cap in the common case, avoiding a needless second downscale.
	/// </summary>
	internal const double DownscaleSafetyMargin = 0.95;

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		NumberHandling = JsonNumberHandling.AllowReadingFromString,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly HttpClient _httpClient;
	private readonly AnthropicOptions _options;
	private readonly ILogger<AnthropicReceiptExtractionService> _logger;

	public AnthropicReceiptExtractionService(
		HttpClient httpClient,
		IOptions<AnthropicOptions> options,
		ILogger<AnthropicReceiptExtractionService> logger)
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

		// Reject oversized images before allocating a base64 buffer (~133% of input). The
		// Anthropic API rejects oversized image inputs with an opaque 400; rejecting here
		// produces a clear ArgumentException at the boundary instead. Mirrors the Ollama
		// guard introduced in RECEIPTS-640.
		if (imageBytes.Length > _options.MaxImageBytes)
		{
			throw new ArgumentException(
				$"Image is {imageBytes.Length} bytes, exceeding the configured maximum of {_options.MaxImageBytes} bytes.",
				nameof(imageBytes));
		}

		ReceiptExtractionPromptValue prompt = ReceiptExtractionPrompt.Current;

		// Stamp every log record raised during this request with the prompt version and the
		// model tag, so a regression can be traced back to the exact prompt + model that
		// produced it. Same shape the Ollama service emits — provider-agnostic by design
		// (RECEIPTS-639 keeps these scopes provider-agnostic).
		using IDisposable? promptScope = _logger.BeginScope(new Dictionary<string, object>
		{
			["VlmPromptVersion"] = prompt.Version,
			["VlmModel"] = _options.Model,
			["VlmProvider"] = "anthropic",
		});

		_logger.LogDebug(
			"Extracting receipt via Anthropic VLM (model={Model}, promptVersion={PromptVersion}, bytes={Bytes})",
			_options.Model, prompt.Version, imageBytes.Length);

		// Downscale before base64 encoding (RECEIPTS-654). Anthropic's per-image API limit
		// is 5 MB *after* base64 encoding (5,242,880 bytes), so a 200 DPI rasterized PDF
		// from PdfConversionService routinely produces 5–8 MB raw PNGs that overflow the
		// limit. Rather than fail the upload outright (the API returns an opaque 400),
		// iteratively resample the PNG until it fits under MaxRawImageBytes — which is
		// chosen to leave headroom under the 5 MB base64 ceiling. This sits inside the
		// service rather than PdfConversionService because the cap is provider-specific
		// (Ollama has no such limit; Anthropic does).
		byte[] payloadBytes = DownscaleIfNeeded(imageBytes);

		string base64 = Convert.ToBase64String(payloadBytes);
		AnthropicMessagesRequest request = BuildRequest(prompt, base64);

		// Per-attempt timeout is enforced by the resilience pipeline (Polly Timeout strategy
		// registered in InfrastructureService.AddAnthropicVlmClient). Each retry receives a
		// fresh AnthropicOptions.TimeoutSeconds budget — same pattern as RECEIPTS-630.
		AnthropicMessagesResponse response;
		string responseBody;
		try
		{
			using HttpRequestMessage httpRequest = new(HttpMethod.Post, "v1/messages")
			{
				Content = JsonContent.Create(request, options: JsonOptions),
			};

			using HttpResponseMessage httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken);
			responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

			if (!httpResponse.IsSuccessStatusCode)
			{
				ThrowFromAnthropicError(httpResponse, responseBody);
			}

			AnthropicMessagesResponse? parsed;
			try
			{
				parsed = JsonSerializer.Deserialize<AnthropicMessagesResponse>(responseBody, JsonOptions);
			}
			catch (JsonException ex)
			{
				throw new InvalidOperationException(
					$"Failed to parse Anthropic VLM response envelope. Raw response (truncated): {Truncate(responseBody, ExceptionMessageMaxChars)}",
					ex);
			}

			response = parsed
				?? throw new InvalidOperationException("Anthropic VLM returned a null response envelope.");
		}
		catch (TimeoutRejectedException)
		{
			throw new TimeoutException($"Anthropic VLM call timed out after {_options.TimeoutSeconds}s.");
		}

		if (_options.LogRawResponses)
		{
			// Gated PII-bearing log: store, items, payment method, last-four can all appear in
			// the response body. Default off in production. See RECEIPTS-639 for the rationale.
			_logger.LogDebug("Anthropic VLM raw response: {Response}", responseBody);
		}

		LogUsage(response.Usage);

		AnthropicResponseContent? toolUse = response.Content?
			.FirstOrDefault(c => string.Equals(c.Type, "tool_use", StringComparison.Ordinal)
				&& string.Equals(c.Name, SubmitToolName, StringComparison.Ordinal));

		if (toolUse is null || toolUse.Input is null)
		{
			// The model didn't call our tool. This is rare with tool_choice pinned to a
			// specific tool, but a refusal or an oversize-image rejection can surface as a
			// text-only response. Surface enough detail to debug without leaking PII (the
			// raw body is already truncated to ExceptionMessageMaxChars).
			string? text = response.Content?
				.FirstOrDefault(c => string.Equals(c.Type, "text", StringComparison.Ordinal))?.Text;
			throw new InvalidOperationException(
				$"Anthropic VLM did not call the {SubmitToolName} tool (stop_reason={response.StopReason ?? "null"}). "
				+ $"Text-block fragment: {Truncate(text ?? string.Empty, ExceptionMessageMaxChars)}");
		}

		VlmReceiptPayload payload;
		try
		{
			payload = toolUse.Input.Value.Deserialize<VlmReceiptPayload>(JsonOptions)
				?? throw new InvalidOperationException("Anthropic VLM produced a null receipt payload.");
		}
		catch (JsonException ex)
		{
			throw new InvalidOperationException(
				$"Failed to parse Anthropic VLM tool input as VlmReceiptPayload. Raw input (truncated): "
				+ Truncate(toolUse.Input.Value.GetRawText(), ExceptionMessageMaxChars),
				ex);
		}

		if (payload.SchemaVersion != VlmReceiptPayload.CurrentSchemaVersion)
		{
			// Same fail-fast contract as the Ollama service (RECEIPTS-639): a stale prompt or
			// a model that returns a future schema version must not silently pass through.
			// Don't include the raw payload — this exception message routinely propagates
			// through telemetry and would leak PII.
			throw new InvalidOperationException(
				$"Anthropic VLM payload schema_version mismatch (expected={VlmReceiptPayload.CurrentSchemaVersion}, "
				+ $"actual={payload.SchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "null"}, "
				+ $"promptVersion={prompt.Version}).");
		}

		return OllamaReceiptExtractionService.MapToParsedReceipt(payload);
	}

	/// <summary>
	/// Iteratively downscales a PNG until its raw byte length fits under
	/// <see cref="AnthropicOptions.MaxRawImageBytes"/>. Returns the original buffer
	/// unchanged when the input already fits — the common case for camera/JPEG-derived
	/// uploads. Re-encodes as PNG (lossless) on each pass to preserve OCR-relevant detail;
	/// switching to JPEG would compress more aggressively at the cost of edge sharpness,
	/// which the VLM exploits when reading low-contrast price lines. Throws
	/// <see cref="InvalidOperationException"/> with both the original and the final byte
	/// counts in the message after <see cref="MaxDownscaleAttempts"/> passes still fail
	/// to fit, so the user gets actionable feedback instead of a silent truncation.
	/// </summary>
	internal byte[] DownscaleIfNeeded(byte[] imageBytes)
	{
		if (imageBytes.Length <= _options.MaxRawImageBytes)
		{
			return imageBytes;
		}

		int originalLength = imageBytes.Length;
		int targetBytes = _options.MaxRawImageBytes;
		byte[] current = imageBytes;
		double cumulativeScale = 1.0;

		for (int attempt = 1; attempt <= MaxDownscaleAttempts; attempt++)
		{
			// Compute scale factor from current image, not original — each pass starts
			// from the previous pass's output so the analytical sqrt() relationship still
			// holds. The 0.95 safety multiplier compensates for sub-quadratic byte growth
			// on natural images so a single pass usually suffices.
			double rawScale = Math.Sqrt((double)targetBytes / current.Length);
			double scale = Math.Min(rawScale, 1.0) * DownscaleSafetyMargin;
			cumulativeScale *= scale;

			byte[] resampled = ResamplePng(current, scale);

			if (resampled.Length <= targetBytes)
			{
				// First downscale wins: log once at Info so the operator sees the cap
				// engaged. Subsequent passes are diagnostic-level only — they only happen
				// on pathological inputs. The logged scale is cumulative from the
				// original image (product of per-pass scales), which is the metric an
				// operator actually wants — the per-pass scale is meaningless on its
				// own once attempts > 1.
				_logger.LogInformation(
					"Anthropic VLM image downscaled to fit API cap (originalBytes={OriginalBytes}, finalBytes={FinalBytes}, scale={Scale:F3}, attempts={Attempts})",
					originalLength, resampled.Length, cumulativeScale, attempt);
				return resampled;
			}

			current = resampled;
		}

		throw new InvalidOperationException(
			$"Anthropic VLM image could not be downscaled below the {targetBytes}-byte cap after "
			+ $"{MaxDownscaleAttempts} attempts (original={originalLength} bytes, final={current.Length} bytes). "
			+ $"The image may be too large or contain incompressible noise; reduce the source resolution before retrying.");
	}

	/// <summary>
	/// Resamples a PNG by a linear scale factor and re-encodes as PNG. Caller controls
	/// the scale; this helper is intentionally stateless so unit tests can drive it
	/// directly. Decoding via <see cref="SKBitmap.Decode(byte[])"/> handles any input
	/// SkiaSharp recognises (PNG/JPEG/WEBP/etc.) — useful here because the PdfConversionService
	/// emits PNG today but a future change to JPEG-from-PDFtoImage would still work.
	/// </summary>
	internal static byte[] ResamplePng(byte[] imageBytes, double scale)
	{
		using SKBitmap source = SKBitmap.Decode(imageBytes)
			?? throw new InvalidOperationException(
				"Failed to decode image bytes as a PNG/JPEG/etc. for downscaling.");

		// Floor at 1 pixel per dimension. SkiaSharp.Resize rejects non-positive sizes and
		// pathological scale=0 inputs would otherwise blow up here rather than producing
		// a (still-wrong-but-debuggable) tiny output.
		int newWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
		int newHeight = Math.Max(1, (int)Math.Round(source.Height * scale));

		using SKBitmap resized = source.Resize(
			new SKImageInfo(newWidth, newHeight),
			new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

		if (resized is null)
		{
			throw new InvalidOperationException(
				$"Failed to resample image to {newWidth}x{newHeight} during downscale.");
		}

		using SKImage? image = SKImage.FromBitmap(resized);
		if (image is null)
		{
			// SKImage.FromBitmap returns null on native allocation failure (out-of-memory,
			// invalid bitmap state). Surface a typed exception rather than letting the
			// subsequent .Encode call throw NullReferenceException — same shape as the
			// other null-check guards in this method so the failure is debuggable.
			throw new InvalidOperationException(
				$"Failed to wrap resized {newWidth}x{newHeight} bitmap as an SKImage during downscale.");
		}

		using SKData encoded = image.Encode(SKEncodedImageFormat.Png, quality: 100)
			?? throw new InvalidOperationException("Failed to re-encode image as PNG after downscale.");

		return encoded.ToArray();
	}

	private AnthropicMessagesRequest BuildRequest(ReceiptExtractionPromptValue prompt, string base64)
	{
		// System block carries the prompt and is marked cache_control: ephemeral. The
		// prompt is constant per schema version, so cache hits drop input cost ~10x on
		// repeat scans. The image and any per-receipt content live OUTSIDE the cached
		// block (in the user message) so they don't poison the cache key.
		List<AnthropicSystemBlock> system =
		[
			new AnthropicSystemBlock(
				Type: "text",
				Text: prompt.Text,
				CacheControl: new AnthropicCacheControl("ephemeral")),
		];

		List<AnthropicContentBlock> userContent =
		[
			new AnthropicContentBlock(
				Type: "image",
				Source: new AnthropicImageSource(
					Type: "base64",
					MediaType: "image/png",
					Data: base64)),
			new AnthropicContentBlock(
				Type: "text",
				Text: "Extract this receipt and call the submit_receipt tool with the structured JSON payload."),
		];

		List<AnthropicMessage> messages =
		[
			new AnthropicMessage(Role: "user", Content: userContent),
		];

		List<AnthropicTool> tools =
		[
			new AnthropicTool(
				Name: SubmitToolName,
				Description: "Submit the structured receipt extraction. Call this exactly once with the parsed receipt payload.",
				InputSchema: VlmReceiptToolSchema.SchemaElement),
		];

		AnthropicToolChoice toolChoice = new(Type: "tool", Name: SubmitToolName);

		return new AnthropicMessagesRequest(
			Model: _options.Model,
			MaxTokens: _options.MaxTokens,
			System: system,
			Messages: messages,
			Tools: tools,
			ToolChoice: toolChoice);
	}

	private void LogUsage(AnthropicUsage? usage)
	{
		if (usage is null)
		{
			return;
		}

		// Token + cache telemetry. Useful for cost tracking and confirming prompt caching is
		// actually hitting (cache_read_input_tokens > 0 on subsequent calls within the cache
		// TTL). PII-free — just integers.
		_logger.LogInformation(
			"Anthropic VLM usage: input={InputTokens} output={OutputTokens} cache_creation={CacheCreationInputTokens} cache_read={CacheReadInputTokens}",
			usage.InputTokens ?? 0,
			usage.OutputTokens ?? 0,
			usage.CacheCreationInputTokens ?? 0,
			usage.CacheReadInputTokens ?? 0);
	}

	/// <summary>
	/// Surfaces an Anthropic 4xx/5xx response as a typed exception with the upstream
	/// error <c>type</c>/<c>message</c> when the body parses as the documented error
	/// envelope. Falls back to a generic message that includes the truncated raw body
	/// so unparseable bodies (e.g. Cloudflare-injected HTML) still produce something
	/// debuggable. The exception is always <see cref="InvalidOperationException"/> so
	/// callers can catch a single type — same shape the Ollama service uses.
	/// </summary>
	private static void ThrowFromAnthropicError(HttpResponseMessage response, string responseBody)
	{
		AnthropicErrorEnvelope? envelope = null;
		if (!string.IsNullOrWhiteSpace(responseBody))
		{
			try
			{
				envelope = JsonSerializer.Deserialize<AnthropicErrorEnvelope>(responseBody, JsonOptions);
			}
			catch (JsonException)
			{
				// Fall through — handled below.
			}
		}

		if (envelope?.Error is { } error)
		{
			throw new InvalidOperationException(
				$"Anthropic VLM call failed with HTTP {(int)response.StatusCode}: "
				+ $"type={error.Type ?? "null"}, message={error.Message ?? "null"}");
		}

		throw new InvalidOperationException(
			$"Anthropic VLM call failed with HTTP {(int)response.StatusCode}. "
			+ $"Body (truncated): {Truncate(responseBody, ExceptionMessageMaxChars)}");
	}

	/// <summary>
	/// Truncates <paramref name="value"/> to at most <paramref name="maxChars"/> UTF-16 code
	/// units, appending an ellipsis suffix when the string is cut. Used to keep exception
	/// messages from leaking the entire VLM payload (PII) into telemetry. Mirrors the Ollama
	/// service's helper character-for-character so both providers share a contract.
	/// <para>
	/// If the cut boundary lands between the two halves of a UTF-16 surrogate pair, the high
	/// surrogate is dropped rather than orphaned. An unpaired high surrogate would produce an
	/// ill-formed string that <see cref="System.Text.Json"/> rejects in strict mode, masking
	/// the original exception in telemetry.
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
}
