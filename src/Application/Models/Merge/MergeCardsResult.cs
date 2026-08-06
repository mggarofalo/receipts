namespace Application.Models.Merge;

/// <summary>
/// What a merge actually did.
///
/// This deliberately carries counts rather than a <c>Success</c> flag. A merge whose
/// cards already all sit on the target is idempotent and correct, but it moved nothing —
/// and a bare boolean cannot tell that apart from a merge that deleted two accounts and
/// repointed four hundred transactions. Reporting both as "Cards merged" is what
/// RECEIPTS-893 was about.
///
/// Exactly one of <see cref="Conflicts"/> and the counts is meaningful: when conflicts
/// are present the merge did not run at all, so every count is zero.
/// </summary>
public record MergeCardsResult(
	int AccountsRemoved,
	int CardsMoved,
	int TransactionsRepointed,
	IReadOnlyList<YnabMappingConflict>? Conflicts)
{
	/// <summary>
	/// True when the merge ran and changed nothing — every selected card already
	/// belonged to the target account.
	/// </summary>
	public bool IsNoOp => Conflicts is null
		&& AccountsRemoved == 0
		&& CardsMoved == 0
		&& TransactionsRepointed == 0;

	/// <summary>A merge the database had already satisfied.</summary>
	public static MergeCardsResult NoOp() => new(0, 0, 0, null);

	/// <summary>A merge that was refused pending a YNAB mapping decision; nothing was written.</summary>
	public static MergeCardsResult Conflicted(IReadOnlyList<YnabMappingConflict> conflicts) =>
		new(0, 0, 0, conflicts);
}
