using System.Text.Json.Serialization;

namespace Infrastructure.Ynab.Models;

/// <summary>
/// Response envelope for GET /v1/budgets/{budget_id}/categories.
/// </summary>
internal sealed class YnabCategoriesResponseEnvelope
{
	[JsonPropertyName("data")]
	public YnabCategoriesResponseData Data { get; set; } = null!;
}

internal sealed class YnabCategoriesResponseData
{
	[JsonPropertyName("category_groups")]
	public List<YnabCategoryGroupDto> CategoryGroups { get; set; } = [];
}

internal sealed class YnabCategoryGroupDto
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = string.Empty;

	[JsonPropertyName("name")]
	public string Name { get; set; } = string.Empty;

	[JsonPropertyName("hidden")]
	public bool Hidden { get; set; }

	[JsonPropertyName("deleted")]
	public bool Deleted { get; set; }

	[JsonPropertyName("categories")]
	public List<YnabCategoryDto> Categories { get; set; } = [];
}

internal sealed class YnabCategoryDto
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = string.Empty;

	[JsonPropertyName("category_group_id")]
	public string CategoryGroupId { get; set; } = string.Empty;

	[JsonPropertyName("category_group_name")]
	public string? CategoryGroupName { get; set; }

	[JsonPropertyName("name")]
	public string Name { get; set; } = string.Empty;

	[JsonPropertyName("hidden")]
	public bool Hidden { get; set; }

	[JsonPropertyName("deleted")]
	public bool Deleted { get; set; }
}
