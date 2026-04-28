using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infrastructure.Services;

// ---------------------------------------------------------------------------
// Anthropic Messages API request / response shapes used by
// AnthropicReceiptExtractionService. These intentionally model only the
// subset we need — image input, prompt caching on the system block, tool-use
// for forced-JSON output, and tool-use response parsing. Streaming, batch,
// and non-tool message types are out of scope for this POC (RECEIPTS-652).
//
// Reference: https://docs.anthropic.com/en/api/messages
// ---------------------------------------------------------------------------

internal sealed record AnthropicMessagesRequest(
	[property: JsonPropertyName("model")] string Model,
	[property: JsonPropertyName("max_tokens")] int MaxTokens,
	[property: JsonPropertyName("system")] IReadOnlyList<AnthropicSystemBlock>? System,
	[property: JsonPropertyName("messages")] IReadOnlyList<AnthropicMessage> Messages,
	[property: JsonPropertyName("tools")] IReadOnlyList<AnthropicTool> Tools,
	[property: JsonPropertyName("tool_choice")] AnthropicToolChoice ToolChoice);

/// <summary>
/// Top-level system blocks support <c>cache_control</c>, which is what we use to
/// cache the constant receipt-extraction prompt (cache hits cut input cost ~10x for
/// repeat scans). Image and per-receipt content stay outside the cached block.
/// </summary>
internal sealed record AnthropicSystemBlock(
	[property: JsonPropertyName("type")] string Type,
	[property: JsonPropertyName("text")] string Text,
	[property: JsonPropertyName("cache_control")] AnthropicCacheControl? CacheControl);

internal sealed record AnthropicCacheControl(
	[property: JsonPropertyName("type")] string Type);

internal sealed record AnthropicMessage(
	[property: JsonPropertyName("role")] string Role,
	[property: JsonPropertyName("content")] IReadOnlyList<AnthropicContentBlock> Content);

/// <summary>
/// Polymorphic content block discriminated by <c>type</c>: <c>image</c> for the
/// receipt rasterized PNG, <c>text</c> for any per-request user instruction. Only
/// one of <see cref="Source"/> or <see cref="Text"/> is meaningful for any given
/// type; both are nullable so a single record models both shapes.
/// </summary>
internal sealed record AnthropicContentBlock(
	[property: JsonPropertyName("type")] string Type,
	[property: JsonPropertyName("source")] AnthropicImageSource? Source = null,
	[property: JsonPropertyName("text")] string? Text = null);

internal sealed record AnthropicImageSource(
	[property: JsonPropertyName("type")] string Type,
	[property: JsonPropertyName("media_type")] string MediaType,
	[property: JsonPropertyName("data")] string Data);

/// <summary>
/// Tool definition that forces the model to emit structured JSON conforming to
/// <see cref="VlmReceiptPayload"/>. <c>tool_choice</c> in the parent request pins
/// the response to this tool — no free-form text branch, no JSON-in-text parsing.
/// </summary>
internal sealed record AnthropicTool(
	[property: JsonPropertyName("name")] string Name,
	[property: JsonPropertyName("description")] string Description,
	[property: JsonPropertyName("input_schema")] JsonElement InputSchema);

internal sealed record AnthropicToolChoice(
	[property: JsonPropertyName("type")] string Type,
	[property: JsonPropertyName("name")] string Name);

// ---------------------------------------------------------------------------
// Response shapes
// ---------------------------------------------------------------------------

internal sealed record AnthropicMessagesResponse(
	[property: JsonPropertyName("id")] string? Id,
	[property: JsonPropertyName("model")] string? Model,
	[property: JsonPropertyName("stop_reason")] string? StopReason,
	[property: JsonPropertyName("content")] IReadOnlyList<AnthropicResponseContent>? Content,
	[property: JsonPropertyName("usage")] AnthropicUsage? Usage);

/// <summary>
/// Polymorphic response content discriminated by <c>type</c>. We only emit a
/// tool the model must call, so the only block type we read in production is
/// <c>tool_use</c> with the receipt payload in <see cref="Input"/>. Free-form
/// text blocks are kept in the model so an unexpected response can be surfaced
/// in error messages.
/// </summary>
internal sealed record AnthropicResponseContent(
	[property: JsonPropertyName("type")] string Type,
	[property: JsonPropertyName("text")] string? Text,
	[property: JsonPropertyName("name")] string? Name,
	[property: JsonPropertyName("input")] JsonElement? Input);

/// <summary>
/// Token-usage telemetry. Surfaced via structured logging so cost-per-receipt and
/// cache-hit ratio (input vs cache_read) can be tracked over time.
/// </summary>
internal sealed record AnthropicUsage(
	[property: JsonPropertyName("input_tokens")] int? InputTokens,
	[property: JsonPropertyName("output_tokens")] int? OutputTokens,
	[property: JsonPropertyName("cache_creation_input_tokens")] int? CacheCreationInputTokens,
	[property: JsonPropertyName("cache_read_input_tokens")] int? CacheReadInputTokens);

// ---------------------------------------------------------------------------
// Error shape (returned with 4xx / 5xx)
// ---------------------------------------------------------------------------

internal sealed record AnthropicErrorEnvelope(
	[property: JsonPropertyName("type")] string? Type,
	[property: JsonPropertyName("error")] AnthropicError? Error);

internal sealed record AnthropicError(
	[property: JsonPropertyName("type")] string? Type,
	[property: JsonPropertyName("message")] string? Message);
