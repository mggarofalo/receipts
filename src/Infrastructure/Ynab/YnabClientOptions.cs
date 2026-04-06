namespace Infrastructure.Ynab;

public class YnabClientOptions
{
	public string BaseUrl { get; set; } = "https://api.ynab.com/v1";
	public int BudgetCacheTtlSeconds { get; set; } = 300;
	public int AccountCacheTtlSeconds { get; set; } = 300;
	public int CategoryCacheTtlSeconds { get; set; } = 300;
	public int RateLimitMaxRequests { get; set; } = 200;
	public int RateLimitWindowSeconds { get; set; } = 3600;
}
