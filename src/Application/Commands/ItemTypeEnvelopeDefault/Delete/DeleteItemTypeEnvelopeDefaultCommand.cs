using Application.Interfaces;

namespace Application.Commands.ItemTypeEnvelopeDefault.Delete;

public record DeleteItemTypeEnvelopeDefaultCommand(Guid CategoryId) : ICommand<bool>;
