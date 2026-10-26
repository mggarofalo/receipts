namespace Application.Taxonomy;

public static class HistoricalReclassificationPolicy
{
	public const int MaximumSelectedGroups = 25;
	public const int MaximumChunkSize = 100;

	public static HistoricalOperationState BeginApply(HistoricalOperationState state) => state switch
	{
		HistoricalOperationState.Preview or HistoricalOperationState.Applying => HistoricalOperationState.Applying,
		_ => throw new InvalidOperationException("Only a preview or interrupted apply can begin applying.")
	};

	public static HistoricalOperationState Cancel(HistoricalOperationState state) => state switch
	{
		HistoricalOperationState.Preview or HistoricalOperationState.Applying or HistoricalOperationState.Cancelled => HistoricalOperationState.Cancelled,
		_ => throw new InvalidOperationException("Only a preview or active apply can be cancelled.")
	};

	public static HistoricalOperationState BeginUndo(HistoricalOperationState state) => state switch
	{
		HistoricalOperationState.Completed or HistoricalOperationState.Cancelled or HistoricalOperationState.Undoing => HistoricalOperationState.Undoing,
		_ => throw new InvalidOperationException("Apply must complete or be cancelled before undo begins.")
	};

	public static void ValidateSelection(IReadOnlyList<HistoricalGroupKey> selectedGroups,
		IReadOnlyList<HistoricalReclassificationItem> items)
	{
		if (selectedGroups.Count is < 1 or > MaximumSelectedGroups || selectedGroups.Distinct().Count() != selectedGroups.Count)
		{ throw new ArgumentException("Select between one and 25 distinct exact groups.", nameof(selectedGroups)); }
		HashSet<HistoricalGroupKey> selected = selectedGroups.ToHashSet();
		if (items.Select(item => item.ItemId).Distinct().Count() != items.Count || items.Any(item => !selected.Contains(item.Group)))
		{ throw new ArgumentException("Preview items must be distinct and belong to explicitly selected groups.", nameof(items)); }
		Guid[] operations = items.SelectMany(item => new[] { item.ApplyOperationId, item.UndoOperationId }).ToArray();
		if (operations.Any(id => id == Guid.Empty) || operations.Distinct().Count() != operations.Length)
		{ throw new ArgumentException("Each item needs distinct durable apply and undo operation IDs.", nameof(items)); }
	}

	public static HistoricalDecision Apply(HistoricalReclassificationItem item, Guid expectedTaxonomyRevision,
		HistoricalCurrentState current)
	{
		if (!current.Available) { return new(HistoricalDecisionKind.Skip, "source-unavailable"); }
		if (current.TaxonomyRevisionId != expectedTaxonomyRevision) { return new(HistoricalDecisionKind.Conflict, "taxonomy-changed"); }
		if (current.ItemRevision != item.ItemRevision || current.ReceiptRevision != item.ReceiptRevision || current.Group != item.Group)
		{ return new(HistoricalDecisionKind.Conflict, "source-changed"); }
		if (current.AssignmentId != item.ExpectedAssignmentId) { return new(HistoricalDecisionKind.Conflict, "assignment-changed"); }
		if (item.TargetCategoryId.HasValue && !current.CategorySelectable) { return new(HistoricalDecisionKind.Conflict, "target-unavailable"); }
		if (item.TargetCategoryId == item.PreviousCategoryId) { return new(HistoricalDecisionKind.Skip, "already-selected-category"); }
		return new(HistoricalDecisionKind.Append);
	}

	public static HistoricalDecision Undo(HistoricalReclassificationItem item, Guid? appliedAssignmentId,
		Guid expectedTaxonomyRevision, HistoricalCurrentState current)
	{
		if (appliedAssignmentId == null) { return new(HistoricalDecisionKind.Skip, "not-applied"); }
		if (!current.Available) { return new(HistoricalDecisionKind.Skip, "source-unavailable"); }
		if (current.AssignmentId != appliedAssignmentId) { return new(HistoricalDecisionKind.Conflict, "assignment-no-longer-owned"); }
		if (current.TaxonomyRevisionId != expectedTaxonomyRevision) { return new(HistoricalDecisionKind.Conflict, "taxonomy-changed"); }
		if (item.PreviousCategoryId.HasValue && !current.CategorySelectable) { return new(HistoricalDecisionKind.Conflict, "previous-category-unavailable"); }
		return new(HistoricalDecisionKind.Append);
	}

	public static ClassificationCommand Command(HistoricalReclassificationItem item, Guid revisionId, string reason) =>
		new(item.TargetCategoryId, revisionId, item.ExpectedAssignmentId, item.ApplyOperationId, reason);

	public static ClassificationCommand UndoCommand(HistoricalReclassificationItem item, Guid appliedAssignmentId, Guid revisionId, string reason) =>
		new(item.PreviousCategoryId, revisionId, appliedAssignmentId, item.UndoOperationId, reason);

	public static IReadOnlyList<HistoricalItemResult> NextChunk(HistoricalOperationState state,
		IReadOnlyList<HistoricalItemResult> items, int limit)
	{
		if (limit is < 1 or > MaximumChunkSize) { throw new ArgumentOutOfRangeException(nameof(limit)); }
		return state is HistoricalOperationState.Preview or HistoricalOperationState.Applying
			? items.Where(item => item.State == HistoricalItemState.Pending).Take(limit).ToArray()
			: [];
	}
}
