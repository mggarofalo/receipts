namespace Infrastructure.Entities.Core;

public class YnabSelectedBudgetEntity
{
	public Guid Id { get; set; }
	public string BudgetId { get; set; } = string.Empty;
	public DateTimeOffset UpdatedAt { get; set; }
}
