namespace Application.Models.Ynab;

public record YnabAccountMappingDto(
	Guid Id,
	Guid ReceiptsAccountId,
	string YnabAccountId,
	string YnabAccountName,
	string YnabBudgetId,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);
