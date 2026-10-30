using Application.Interfaces;

namespace Application.Commands.ItemTypeEnvelopeDefault.Set;

public record SetItemTypeEnvelopeDefaultCommand(Guid CategoryId, Guid EnvelopeId) : ICommand<Interfaces.Services.ItemTypeEnvelopeDefault>;
