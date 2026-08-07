using Application.Interfaces;
using Application.Models.NormalizedDescriptions;

namespace Application.Commands.NormalizedDescription.Rename;

/// <summary>
/// Sets or clears a row's display label (RECEIPTS-876). A null
/// <paramref name="DisplayLabel"/> clears it, so the row falls back to showing its matched text.
/// </summary>
/// <remarks>
/// Touches the label only — never CanonicalName, never the embedding. That is what makes a
/// rename unable to change which receipt text resolves to this row.
/// </remarks>
public record RenameNormalizedDescriptionCommand(Guid Id, string? DisplayLabel)
	: ICommand<NormalizedDescriptionDetail>;
