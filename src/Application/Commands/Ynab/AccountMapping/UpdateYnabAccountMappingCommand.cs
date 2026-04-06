using Application.Interfaces;
using MediatR;

namespace Application.Commands.Ynab.AccountMapping;

public record UpdateYnabAccountMappingCommand(
	Guid Id,
	string YnabAccountId,
	string YnabAccountName,
	string YnabBudgetId) : ICommand<Unit>;
