namespace Application.Models.Ynab;

public record YnabEnvelopeMappingDto(
	Guid Id,
	Guid EnvelopeId,
	string EnvelopeName,
	string YnabCategoryId,
	string YnabCategoryName,
	string YnabCategoryGroupName,
	string YnabBudgetId,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);
