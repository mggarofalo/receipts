using Application.Interfaces;
using MediatR;

namespace Application.Commands.Ynab.SelectBudget;

public record SelectYnabBudgetCommand(string BudgetId) : ICommand<Unit>;
