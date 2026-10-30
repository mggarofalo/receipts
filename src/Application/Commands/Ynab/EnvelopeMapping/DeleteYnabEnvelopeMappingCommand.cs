using Application.Interfaces;

namespace Application.Commands.Ynab.EnvelopeMapping;

/// <summary>Removes an envelope's YNAB category link in the selected budget.</summary>
public record DeleteYnabEnvelopeMappingCommand(Guid EnvelopeId) : ICommand<bool>;
