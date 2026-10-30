using Application.Interfaces;

namespace Application.Commands.Occasion.Update;

/// <summary>Renames or archives (IsActive = false) an existing occasion.</summary>
public record UpdateOccasionCommand(Domain.Envelopes.Occasion Occasion) : ICommand<bool>;
