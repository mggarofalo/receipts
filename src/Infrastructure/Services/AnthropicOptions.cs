using System.ComponentModel.DataAnnotations;

namespace Infrastructure.Services;

/// <summary>
/// Options for the Anthropic-backed <see cref="AnthropicReceiptExtractionService"/>.
/// Bound from the <c>Anthropic</c> configuration section. The API key is the only required
/// value — every other knob has a sensible default. Validated at startup via
/// <c>IOptions&lt;AnthropicOptions&gt;</c> + <c>ValidateDataAnnotations().ValidateOnStart()</c>
/// so a missing key fails the host before the first user upload (RECEIPTS-652, mirroring
/// RECEIPTS-638's pattern).
/// </summary>
public sealed class AnthropicOptions
{
	/// <summary>
	/// Default Anthropic Messages API base URL. The Anthropic-hosted service has no
	/// region affinity — every customer hits the same global endpoint. Override only
	/// for local mocking (HTTP test handlers) or for a future enterprise proxy.
	/// </summary>
	public const string DefaultBaseUrl = "https://api.anthropic.com";

	/// <summary>
	/// Default Anthropic API version pinned in the <c>anthropic-version</c> request
	/// header. The Messages API + tool-use + prompt caching are all stable on this
	/// version; bumping requires a code review per Anthropic's migration notes.
	/// </summary>
	public const string DefaultApiVersion = "2023-06-01";

	/// <summary>
	/// Default model tag used when <c>Anthropic:Model</c> is not explicitly configured.
	/// Pinned to the latest Claude Haiku family member at implementation time so future
	/// model swaps require an explicit config change rather than silently shifting under
	/// production traffic.
	/// </summary>
	public const string DefaultModel = "claude-haiku-4-5";

	/// <summary>
	/// Default upper bound on the raw image byte length accepted by the extraction
	/// service. Base64 inflates the request body by ~33%, and the Anthropic API rejects
	/// image inputs larger than ~5 MB after encoding (the public limit at the time of
	/// writing is 5 MB per image). 15 MB raw is a deliberately permissive default — the
	/// production deployment should tune this down once empirical p95 image sizes are
	/// known. See RECEIPTS-640 for the rationale on rejecting before the encode step.
	/// </summary>
	public const int DefaultMaxImageBytes = 15 * 1024 * 1024;

	/// <summary>
	/// Anthropic API key (<c>x-api-key</c> request header). Bound from <c>Anthropic:ApiKey</c>
	/// (or, in deployed environments, the <c>ANTHROPIC_API_KEY</c> env var via the standard
	/// <c>:</c>-to-<c>__</c> mapping). Required — the service cannot operate without it.
	/// Stored as a plain string in memory for the lifetime of the host because the
	/// underlying <see cref="HttpClient"/> already keeps it in the default request headers.
	/// </summary>
	[Required(AllowEmptyStrings = false)]
	public string ApiKey { get; set; } = string.Empty;

	/// <summary>
	/// Anthropic Messages API base URL. Defaults to the public endpoint. Override only
	/// when wiring a test handler or when targeting a future enterprise proxy. The
	/// <see cref="HttpClient.BaseAddress"/> is set from this value during DI registration.
	/// </summary>
	[Required(AllowEmptyStrings = false)]
	public string BaseUrl { get; set; } = DefaultBaseUrl;

	/// <summary>
	/// Anthropic API version pinned in the <c>anthropic-version</c> request header.
	/// Updating this requires a code review per Anthropic's migration notes.
	/// </summary>
	[Required(AllowEmptyStrings = false)]
	public string ApiVersion { get; set; } = DefaultApiVersion;

	/// <summary>
	/// Model tag passed to Anthropic's <c>/v1/messages</c> endpoint. Defaults to
	/// <see cref="DefaultModel"/>. Override via <c>Anthropic:Model</c> when running
	/// against a specific Haiku revision (e.g. for back-compat regression sweeps).
	/// </summary>
	[Required(AllowEmptyStrings = false)]
	public string Model { get; set; } = DefaultModel;

	/// <summary>
	/// Upper bound on tokens in the model's response. Tool-use responses (the only mode
	/// we use here) typically run a few hundred tokens for a typical receipt; 4096 leaves
	/// generous headroom for a long Walmart roll. The Anthropic API rejects requests
	/// where this is missing, so a default is required.
	/// </summary>
	[Range(256, 16_384)]
	public int MaxTokens { get; set; } = 4096;

	/// <summary>
	/// Per-attempt timeout in seconds. Each retry receives a fresh budget (the
	/// resilience pipeline composes Retry around Timeout — same pattern as the Ollama
	/// client, RECEIPTS-630). Range is 1..600 to keep operators from accidentally
	/// configuring an infinite timeout via mis-typed config; the default reflects a
	/// realistic upper bound for a single Haiku Vision call on a large rasterized PDF
	/// (typically ~5-20s, but cold starts on a fresh deploy can spike).
	/// </summary>
	[Range(1, 600)]
	public int TimeoutSeconds { get; set; } = 120;

	/// <summary>
	/// Maximum image byte length accepted by <c>AnthropicReceiptExtractionService.ExtractAsync</c>.
	/// Inputs larger than this throw <see cref="ArgumentException"/> before any base64 encoding
	/// happens, protecting both the client (memory) and the Anthropic API (request body limit).
	/// See RECEIPTS-640 for the original rationale on the Ollama side.
	/// </summary>
	[Range(1, int.MaxValue)]
	public int MaxImageBytes { get; set; } = DefaultMaxImageBytes;

	/// <summary>
	/// When <c>true</c>, the raw Anthropic response body is logged at <c>Debug</c> level after
	/// each successful call. The raw body contains receipt PII (store name, items, payment
	/// method, last-four card digits) so this MUST stay <c>false</c> in production. It exists
	/// for local diagnostics and the <c>VlmEval</c> tool only. See RECEIPTS-639.
	/// </summary>
	public bool LogRawResponses { get; set; }
}
