using System.Text.Json.Serialization;

namespace Infrastructure.Ynab.Models;

/// <summary>
/// Response envelope for GET /v1/budgets.
/// </summary>
internal sealed class YnabBudgetsResponseEnvelope
{
	[JsonPropertyName("data")]
	public YnabBudgetsResponseData Data { get; set; } = null!;
}

internal sealed class YnabBudgetsResponseData
{
	[JsonPropertyName("budgets")]
	public List<YnabBudgetSummaryDto> Budgets { get; set; } = [];
}

internal sealed class YnabBudgetSummaryDto
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = string.Empty;

	[JsonPropertyName("name")]
	public string Name { get; set; } = string.Empty;

	[JsonPropertyName("last_modified_on")]
	public string? LastModifiedOn { get; set; }
}
