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
	public Guid TargetAccountId { get; }
	public IReadOnlyList<Guid> SourceCardIds { get; }
	public Guid? YnabMappingWinnerAccountId { get; }

	public const string TargetIdCannotBeEmpty = "Target account id cannot be empty.";
	public const string SourceCardIdsCannotBeEmpty = "Source card ids cannot be empty.";

	public PreviewMergeCardsQuery(Guid targetAccountId, IReadOnlyList<Guid> sourceCardIds, Guid? ynabMappingWinnerAccountId = null)
	{
		if (targetAccountId == Guid.Empty)
		{
			throw new ArgumentException(TargetIdCannotBeEmpty, nameof(targetAccountId));
		}

		if (sourceCardIds is null || sourceCardIds.Count == 0)
		{
			throw new ArgumentException(SourceCardIdsCannotBeEmpty, nameof(sourceCardIds));
		}

		TargetAccountId = targetAccountId;
		SourceCardIds = sourceCardIds;
		YnabMappingWinnerAccountId = ynabMappingWinnerAccountId;
	}
}
