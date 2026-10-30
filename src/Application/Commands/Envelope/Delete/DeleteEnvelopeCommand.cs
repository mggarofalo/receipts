using Application.Interfaces;

namespace Application.Commands.Envelope.Delete;

public record DeleteEnvelopeCommand(Guid Id) : ICommand<bool>;
