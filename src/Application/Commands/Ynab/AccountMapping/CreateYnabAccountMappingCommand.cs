using Application.Interfaces;
using Application.Models.Ynab;

namespace Application.Commands.Ynab.AccountMapping;

public record CreateYnabAccountMappingCommand(
	Guid ReceiptsAccountId,
	string YnabAccountId,
	string YnabAccountName,
	string YnabBudgetId) : ICommand<YnabAccountMappingDto>;
