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

	public const string OcrEngine = "Ocr:Engine";
	public const string TessdataPath = "Ocr:TessdataPath";
	public const string OcrTimeoutSeconds = "Ocr:TimeoutSeconds";
	public const string OcrMaxImageBytes = "Ocr:MaxImageBytes";

	public const string YnabPat = "YNAB_PAT";
}

