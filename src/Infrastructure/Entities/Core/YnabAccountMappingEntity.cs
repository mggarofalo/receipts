namespace Infrastructure.Entities.Core;

public class YnabAccountMappingEntity
{
	public Guid Id { get; set; }
	public Guid ReceiptsAccountId { get; set; }
	public string YnabAccountId { get; set; } = string.Empty;
	public string YnabAccountName { get; set; } = string.Empty;
	public string YnabBudgetId { get; set; } = string.Empty;
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset UpdatedAt { get; set; }
	public virtual AccountEntity? Account { get; set; }
}
