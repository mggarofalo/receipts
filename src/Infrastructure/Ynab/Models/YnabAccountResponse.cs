using System.Text.Json.Serialization;

namespace Infrastructure.Ynab.Models;

/// <summary>
/// Response envelope for GET /v1/budgets/{budget_id}/accounts.
/// </summary>
internal sealed class YnabAccountsResponseEnvelope
{
	[JsonPropertyName("data")]
	public YnabAccountsResponseData Data { get; set; } = null!;
}

internal sealed class YnabAccountsResponseData
{
	[JsonPropertyName("accounts")]
	public List<YnabAccountDto> Accounts { get; set; } = [];
}

internal sealed class YnabAccountDto
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = string.Empty;

	[JsonPropertyName("name")]
	public string Name { get; set; } = string.Empty;

	[JsonPropertyName("type")]
	public string Type { get; set; } = string.Empty;

	[JsonPropertyName("on_budget")]
	public bool OnBudget { get; set; }

	[JsonPropertyName("closed")]
	public bool Closed { get; set; }

	[JsonPropertyName("balance")]
	public long Balance { get; set; }

	[JsonPropertyName("deleted")]
	public bool Deleted { get; set; }
}
