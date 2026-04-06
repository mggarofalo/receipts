using Application.Interfaces;
using Application.Models.Ynab;

namespace Application.Commands.Ynab.CategoryMapping;

public record CreateYnabCategoryMappingCommand(
	string ReceiptsCategory,
	string YnabCategoryId,
	string YnabCategoryName,
	string YnabCategoryGroupName,
	string YnabBudgetId) : ICommand<YnabCategoryMappingDto>;
