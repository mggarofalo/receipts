namespace Common;

public static class ConfigurationVariables
{
	public const string PostgresHost = "POSTGRES_HOST";
	public const string PostgresPort = "POSTGRES_PORT";
	public const string PostgresUser = "POSTGRES_USER";
	public const string PostgresPassword = "POSTGRES_PASSWORD";
	public const string PostgresDb = "POSTGRES_DB";

	// Aspire-injected connection string name (set via WithReference(db) in AppHost)
	public const string AspireConnectionStringName = "receiptsdb";

	public const string JwtKey = "Jwt:Key";
	public const string JwtIssuer = "Jwt:Issuer";
	public const string JwtAudience = "Jwt:Audience";

	public const string ImageStoragePath = "ImageStorage:Path";

	public const string AdminSeedEmail = "AdminSeed:Email";
	public const string AdminSeedPassword = "AdminSeed:Password";
	public const string AdminSeedFirstName = "AdminSeed:FirstName";
	public const string AdminSeedLastName = "AdminSeed:LastName";

	// Ollama host for the VLM-based receipt extraction pipeline (RECEIPTS-616 epic).
	public const string OllamaBaseUrl = "Ollama:BaseUrl";

	// VLM-based receipt extraction (RECEIPTS-618). Explicit override of the Ollama base URL,
	// the model tag, and the per-call timeout.
	public const string OcrVlmOllamaUrl = "Ocr:Vlm:OllamaUrl";
	public const string OcrVlmModel = "Ocr:Vlm:Model";
	public const string OcrVlmTimeoutSeconds = "Ocr:Vlm:TimeoutSeconds";
	public const string OcrVlmSection = "Ocr:Vlm";

	/// <summary>
	/// VLM provider switch (RECEIPTS-652). Allowed values:
	/// <c>ollama</c> (default) selects <c>OllamaReceiptExtractionService</c>;
	/// <c>anthropic</c> selects <c>AnthropicReceiptExtractionService</c>.
	/// Both register <c>IReceiptExtractionService</c> — only one provider is active at a time.
	/// </summary>
	public const string OcrVlmProvider = "Ocr:Vlm:Provider";

	// Anthropic Messages API config section (RECEIPTS-652). The API key is the only required
	// field; everything else has a sensible default in AnthropicOptions.
	public const string AnthropicSection = "Anthropic";
	public const string AnthropicApiKey = "Anthropic:ApiKey";
	public const string AnthropicModel = "Anthropic:Model";

	// PDF conversion thresholds (RECEIPTS-638). Per-environment tuning of the PDF page-count
	// budget, etc. Bound via IOptions<PdfConversionOptions> with DataAnnotations validation.
	public const string PdfConversionSection = "PdfConversion";

	// Image validation thresholds (RECEIPTS-638). Per-environment tuning of the maximum
	// pixel dimensions accepted for receipt-image uploads. Bound via
	// IOptions<ImageValidationOptions> with DataAnnotations validation.
	public const string ImageValidationSection = "ImageValidation";

	public const string YnabPat = "YNAB_PAT";
}

