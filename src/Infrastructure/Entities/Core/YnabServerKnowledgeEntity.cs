namespace Infrastructure.Entities.Core;

public class YnabServerKnowledgeEntity
{
	public string BudgetId { get; set; } = string.Empty;
	public long ServerKnowledge { get; set; }
	public DateTimeOffset UpdatedAt { get; set; }
}
