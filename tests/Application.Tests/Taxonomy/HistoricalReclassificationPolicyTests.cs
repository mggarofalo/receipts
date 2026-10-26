using Application.Taxonomy;

namespace Application.Tests.Taxonomy;

public class HistoricalReclassificationPolicyTests
{
	private static readonly Guid Revision = Guid.NewGuid();
	private static readonly HistoricalGroupKey Group = new("Store", "MILK 2L", null);
	private static HistoricalReclassificationItem Item() => new(Guid.NewGuid(), Guid.NewGuid(), Group,
		Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
	private static HistoricalCurrentState Current(HistoricalReclassificationItem item) =>
		new(true, item.Group, item.ItemRevision, item.ReceiptRevision, item.ExpectedAssignmentId, Revision, true);

	[Fact]
	public void ExplicitManualRefinementUsesExistingAssignmentCompareAndSwap()
	{
		HistoricalReclassificationItem item = Item();
		HistoricalDecision actual = HistoricalReclassificationPolicy.Apply(item, Revision, Current(item));
		Assert.Equal(HistoricalDecisionKind.Append, actual.Kind);
		Assert.Equal(new ClassificationCommand(item.TargetCategoryId, Revision, item.ExpectedAssignmentId,
			item.ApplyOperationId, "historical refinement"),
			HistoricalReclassificationPolicy.Command(item, Revision, "historical refinement"));
	}

	[Fact]
	public void ApplyRejectsStaleSourceTaxonomyAndAssignmentIndependently()
	{
		HistoricalReclassificationItem item = Item();
		HistoricalCurrentState current = Current(item);
		HistoricalCurrentState[] changed = [current with { ItemRevision = Guid.NewGuid() },
			current with { ReceiptRevision = Guid.NewGuid() }, current with { Group = Group with { RawDescription = "milk 2l" } },
			current with { AssignmentId = Guid.NewGuid() }, current with { TaxonomyRevisionId = Guid.NewGuid() }];
		foreach (HistoricalCurrentState state in changed)
		{
			Assert.Equal(HistoricalDecisionKind.Conflict, HistoricalReclassificationPolicy.Apply(item, Revision, state).Kind);
		}
		Assert.Equal(HistoricalDecisionKind.Skip, HistoricalReclassificationPolicy.Apply(item, Revision, current with { Available = false }).Kind);
	}

	[Fact]
	public void UndoOnlyAppendsWhenTheExactAppliedAssignmentIsStillCurrent()
	{
		HistoricalReclassificationItem item = Item();
		Guid applied = Guid.NewGuid();
		HistoricalCurrentState owned = Current(item) with { AssignmentId = applied };
		Assert.Equal(HistoricalDecisionKind.Append, HistoricalReclassificationPolicy.Undo(item, applied, Revision, owned).Kind);
		Assert.Equal(HistoricalDecisionKind.Conflict, HistoricalReclassificationPolicy.Undo(item, applied, Revision, Current(item)).Kind);
		Assert.Equal(HistoricalDecisionKind.Skip, HistoricalReclassificationPolicy.Undo(item, null, Revision, owned).Kind);
		ClassificationCommand actual = HistoricalReclassificationPolicy.UndoCommand(item, applied, Revision, "undo refinement");
		Assert.Equal(item.PreviousCategoryId, actual.CategoryId);
		Assert.Equal(applied, actual.ExpectedAssignmentId);
		Assert.Equal(item.UndoOperationId, actual.OperationId);
	}

	[Fact]
	public void ExactGroupSelectionDoesNotIncludeUnseenGroupsOrNormalizeRawText()
	{
		HistoricalReclassificationItem item = Item();
		HistoricalReclassificationItem unseen = Item() with { Group = Group with { ItemCode = "123" } };
		Assert.Throws<ArgumentException>(() => HistoricalReclassificationPolicy.ValidateSelection([Group], [item, unseen]));
		Assert.Throws<ArgumentException>(() => HistoricalReclassificationPolicy.ValidateSelection([Group], [item, item]));
		HistoricalReclassificationPolicy.ValidateSelection([Group], [item]);
	}

	[Fact]
	public void RestartUsesPendingRowsOnlyAndCancellationStopsNewChunks()
	{
		HistoricalItemResult applied = new(Item(), HistoricalItemState.Applied, Guid.NewGuid(), null);
		HistoricalItemResult pending = new(Item(), HistoricalItemState.Pending, null, null);
		HistoricalItemResult conflict = new(Item(), HistoricalItemState.Conflicted, null, "assignment-changed");
		Assert.Equal(new[] { pending }, HistoricalReclassificationPolicy.NextChunk(HistoricalOperationState.Applying, [applied, pending, conflict], 1));
		Assert.Empty(HistoricalReclassificationPolicy.NextChunk(HistoricalOperationState.Cancelled, [pending], 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => HistoricalReclassificationPolicy.NextChunk(HistoricalOperationState.Applying, [pending], 101));
	}

	[Fact]
	public void LifecycleAllowsRestartAndUndoAfterPartialCancellationButNeverResumesCancelledApply()
	{
		Assert.Equal(HistoricalOperationState.Applying, HistoricalReclassificationPolicy.BeginApply(HistoricalOperationState.Preview));
		Assert.Equal(HistoricalOperationState.Applying, HistoricalReclassificationPolicy.BeginApply(HistoricalOperationState.Applying));
		Assert.Equal(HistoricalOperationState.Cancelled, HistoricalReclassificationPolicy.Cancel(HistoricalOperationState.Applying));
		Assert.Throws<InvalidOperationException>(() => HistoricalReclassificationPolicy.BeginApply(HistoricalOperationState.Cancelled));
		Assert.Equal(HistoricalOperationState.Undoing, HistoricalReclassificationPolicy.BeginUndo(HistoricalOperationState.Cancelled));
		Assert.Equal(HistoricalOperationState.Undoing, HistoricalReclassificationPolicy.BeginUndo(HistoricalOperationState.Completed));
		Assert.Throws<InvalidOperationException>(() => HistoricalReclassificationPolicy.BeginUndo(HistoricalOperationState.Applying));
	}

	[Fact]
	public void NoOpAndUnclassifiedRestorationDoNotNeedSelectableCategory()
	{
		HistoricalReclassificationItem item = Item();
		HistoricalReclassificationItem noOp = item with { TargetCategoryId = item.PreviousCategoryId };
		Assert.Equal(HistoricalDecisionKind.Skip, HistoricalReclassificationPolicy.Apply(noOp, Revision, Current(item)).Kind);
		Guid applied = Guid.NewGuid();
		Assert.Equal(HistoricalDecisionKind.Append, HistoricalReclassificationPolicy.Undo(item with { PreviousCategoryId = null },
			applied, Revision, Current(item) with { AssignmentId = applied, CategorySelectable = false }).Kind);
	}

	[Fact]
	public void UndoRefusesNewTaxonomyOrUnavailablePreviousCategory()
	{
		HistoricalReclassificationItem item = Item();
		Guid applied = Guid.NewGuid();
		HistoricalCurrentState current = Current(item) with { AssignmentId = applied };
		Assert.Equal(HistoricalDecisionKind.Conflict, HistoricalReclassificationPolicy.Undo(item, applied, Revision,
			current with { TaxonomyRevisionId = Guid.NewGuid() }).Kind);
		Assert.Equal(HistoricalDecisionKind.Conflict, HistoricalReclassificationPolicy.Undo(item, applied, Revision,
			current with { CategorySelectable = false }).Kind);
	}
}
