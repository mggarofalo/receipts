using Application.Interfaces;
using Application.Models.Merge;

namespace Application.Queries.Core.Card;

/// <summary>
/// Asks what a merge would do without doing it. Validates its inputs exactly as
/// <c>MergeCardsIntoAccountCommand</c> does, so a request the merge would refuse to
/// construct is refused here too (RECEIPTS-889).
/// </summary>
public record PreviewMergeCardsQuery : IQuery<MergeCardsPreview>
{
	/// <summary>
	/// The account the cards would move to, or null to preview a merge into an account
	/// that does not exist yet. The merge dialog's "New account" mode needs the latter:
	/// it has to know the selection is valid <em>before</em> creating the account, or a
	/// rejected merge strands an empty one nobody can find (RECEIPTS-902).
	/// </summary>
	public Guid? TargetAccountId { get; }
	public IReadOnlyList<Guid> SourceCardIds { get; }
	public Guid? YnabMappingWinnerAccountId { get; }

	public const string SourceCardIdsCannotBeEmpty = "Source card ids cannot be empty.";

	public PreviewMergeCardsQuery(Guid? targetAccountId, IReadOnlyList<Guid> sourceCardIds, Guid? ynabMappingWinnerAccountId = null)
	{
		if (sourceCardIds is null || sourceCardIds.Count == 0)
		{
			throw new ArgumentException(SourceCardIdsCannotBeEmpty, nameof(sourceCardIds));
		}

		// Guid.Empty is how an omitted uuid arrives from the wire; treat it as "no target"
		// rather than as a real id that will never be found.
		TargetAccountId = targetAccountId == Guid.Empty ? null : targetAccountId;
		SourceCardIds = sourceCardIds;
		YnabMappingWinnerAccountId = ynabMappingWinnerAccountId;
	}
}
