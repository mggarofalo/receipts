using Application.Interfaces;

namespace Application.Commands.Occasion.Delete;

public record DeleteOccasionCommand(Guid Id) : ICommand<bool>;
