using Application.Interfaces;
using Application.Models.Ynab;

namespace Application.Queries.Core.Ynab;

public record GetYnabCategoriesQuery(string BudgetId) : IQuery<List<YnabCategory>>;
