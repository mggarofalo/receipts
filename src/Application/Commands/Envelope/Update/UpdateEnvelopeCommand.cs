using Application.Interfaces;

namespace Application.Commands.Envelope.Update;

/// <summary>Renames or archives (IsActive = false) an existing envelope.</summary>
public record UpdateEnvelopeCommand(Domain.Envelopes.Envelope Envelope) : ICommand<bool>;
