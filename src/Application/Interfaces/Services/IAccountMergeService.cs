using Application.Models.Merge;

namespace Application.Interfaces.Services;

public interface IAccountMergeService
{
	Task<MergeCardsResult> MergeCardsAsync(
		Guid targetAccountId,
		IReadOnlyList<Guid> sourceCardIds,
		Guid? ynabMappingWinnerAccountId,
		CancellationToken cancellationToken);

	/// <summary>
	/// Reports what <see cref="MergeCardsAsync"/> would do, writing nothing.
	///
	/// Runs the same validation, so a request the merge would reject is rejected here
	/// too — the preview cannot promise an outcome the merge would refuse to deliver.
	/// </summary>
	Task<MergeCardsPreview> PreviewMergeCardsAsync(
		Guid targetAccountId,
		IReadOnlyList<Guid> sourceCardIds,
		Guid? ynabMappingWinnerAccountId,
		CancellationToken cancellationToken);
}
