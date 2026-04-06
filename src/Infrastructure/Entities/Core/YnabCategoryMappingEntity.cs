namespace Infrastructure.Entities.Core;

public class YnabCategoryMappingEntity
{
	public Guid Id { get; set; }
	public string ReceiptsCategory { get; set; } = string.Empty;
	public string YnabCategoryId { get; set; } = string.Empty;
	public string YnabCategoryName { get; set; } = string.Empty;
	public string YnabCategoryGroupName { get; set; } = string.Empty;
	public string YnabBudgetId { get; set; } = string.Empty;
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset UpdatedAt { get; set; }
}
