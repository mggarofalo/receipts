namespace Application.Models.Ynab;

public record YnabCategoryMappingDto(
	Guid Id,
	string ReceiptsCategory,
	string YnabCategoryId,
	string YnabCategoryName,
	string YnabCategoryGroupName,
	string YnabBudgetId,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt)
{
	public Guid? ReceiptsCategoryId { get; init; }
	public string LegacyResolution { get; init; } = "Unmatched";
	public string LegacyCandidateCategoryIds { get; init; } = "[]";
}
