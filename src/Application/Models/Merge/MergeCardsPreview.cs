namespace Application.Models.Merge;

/// <summary>
/// What a merge would do, computed without writing anything.
///
/// Merging is irreversible and there is no undo: it repoints every transaction of each
/// source account — including soft-deleted ones — and then deletes those accounts. The
/// dialog's only warning used to be one line of prose, so the user committed without
/// knowing how much was about to move or what was about to disappear (RECEIPTS-889).
///
/// <see cref="Conflicts"/> is populated exactly when the sources carry differing YNAB
/// mappings and no winner has been nominated; the merge cannot proceed until one is, so
/// the rest of the preview is left at its defaults in that case.
/// </summary>
public record MergeCardsPreview(
	IReadOnlyList<MergeCardsPreviewAccount> AccountsToRemove,
	int CardsToMove,
	int TransactionsToRepoint,
	int TrashedTransactionsToRepoint,
	MergeCardsPreviewMapping? SurvivingYnabMapping,
	IReadOnlyList<YnabMappingConflict>? Conflicts)
{
	/// <summary>True when the merge would change nothing — every card already sits on the target.</summary>
	public bool IsNoOp => Conflicts is null
		&& AccountsToRemove.Count == 0
		&& CardsToMove == 0
		&& TransactionsToRepoint == 0
		&& TrashedTransactionsToRepoint == 0;

	/// <summary>A merge the database has already satisfied.</summary>
	public static MergeCardsPreview NoOp() => new([], 0, 0, 0, null, null);

	/// <summary>Blocked pending a YNAB mapping decision; nothing else is meaningful yet.</summary>
	public static MergeCardsPreview Conflicted(IReadOnlyList<YnabMappingConflict> conflicts) =>
		new([], 0, 0, 0, null, conflicts);
}

/// <summary>An account the merge would empty and then delete, named so the user can recognise it.</summary>
public record MergeCardsPreviewAccount(Guid Id, string Name);

/// <summary>The YNAB mapping that would survive the merge, and the account it would end up on.</summary>
public record MergeCardsPreviewMapping(
	Guid FromAccountId,
	string FromAccountName,
	string YnabAccountName);
