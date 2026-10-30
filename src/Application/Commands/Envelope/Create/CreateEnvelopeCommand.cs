using Application.Interfaces;

namespace Application.Commands.Envelope.Create;

public record CreateEnvelopeCommand(Domain.Envelopes.Envelope Envelope) : ICommand<Domain.Envelopes.Envelope>;
