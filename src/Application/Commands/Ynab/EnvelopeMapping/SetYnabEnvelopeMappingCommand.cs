using Application.Interfaces;
using Application.Models.Ynab;

namespace Application.Commands.Ynab.EnvelopeMapping;

/// <summary>Links an envelope to a YNAB category in the selected budget, replacing any existing link.</summary>
public record SetYnabEnvelopeMappingCommand(Guid EnvelopeId, string YnabBudgetId, string YnabCategoryId,
	string YnabCategoryName, string YnabCategoryGroupName) : ICommand<YnabEnvelopeMappingDto>;
