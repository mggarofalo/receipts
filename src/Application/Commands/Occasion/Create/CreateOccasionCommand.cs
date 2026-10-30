using Application.Interfaces;

namespace Application.Commands.Occasion.Create;

public record CreateOccasionCommand(Domain.Envelopes.Occasion Occasion) : ICommand<Domain.Envelopes.Occasion>;
