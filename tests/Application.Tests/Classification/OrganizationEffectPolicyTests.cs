using Application.Classification;
using Application.Taxonomy;
using FluentAssertions;

namespace Application.Tests.Classification;

public sealed class OrganizationEffectPolicyTests
{
	[Fact]
	public void UnassignedPurchaseIsEligible() =>
		OrganizationEffectPolicy.Eligibility(null, true, false, false)
			.Should().Be(OrganizationEligibility.Eligible);

	[Theory]
	[InlineData("legacy_import", null)]
	[InlineData("legacy_write", "unresolved_path")]
	public void UnresolvedLegacyObservationIsEligible(string source, string? resolution) =>
		OrganizationEffectPolicy.Eligibility(Assignment(source, null, resolution), true, false, false)
			.Should().Be(OrganizationEligibility.Eligible);

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ManualChoiceIncludingExplicitClearIsProtected(bool selected) =>
		OrganizationEffectPolicy.Eligibility(Assignment("manual", selected ? Guid.NewGuid() : null), true, false, false)
			.Should().Be(OrganizationEligibility.ManualChoice);

	[Theory]
	[InlineData("legacy_import")]
	[InlineData("legacy_write")]
	[InlineData("approved_mapping")]
	[InlineData("automation")]
	[InlineData("unknown")]
	public void AnySelectedCategoryIsProtected(string source) =>
		OrganizationEffectPolicy.Eligibility(Assignment(source, Guid.NewGuid(), "unresolved_path"), true, false, false)
			.Should().Be(OrganizationEligibility.ExistingAssignment);

	[Theory]
	[InlineData("legacy_write", null)]
	[InlineData("legacy_write", "resolved_path")]
	[InlineData("approved_mapping", null)]
	[InlineData("automation", null)]
	[InlineData("unknown", "unresolved_path")]
	public void NullCategoryWithoutKnownUnresolvedProvenanceIsProtected(string source, string? resolution) =>
		OrganizationEffectPolicy.Eligibility(Assignment(source, null, resolution), true, false, false)
			.Should().Be(OrganizationEligibility.UnknownProvenance);

	[Theory]
	[InlineData(false, false, false, OrganizationEligibility.Unavailable)]
	[InlineData(true, true, false, OrganizationEligibility.LeaveAsIs)]
	[InlineData(true, false, true, OrganizationEligibility.Suppressed)]
	public void UnavailableExcludedAndSuppressedPurchasesRemainUnchanged(bool available, bool leaveAsIs,
		bool suppressed, OrganizationEligibility expected) =>
		OrganizationEffectPolicy.Eligibility(null, available, leaveAsIs, suppressed).Should().Be(expected);

	private static readonly HistoricalReclassificationItem Item = new(Guid.NewGuid(), Guid.NewGuid(),
		new("Store", " Milk ", null), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
		Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
	private static readonly Guid AppliedAssignment = Guid.NewGuid();
	private static readonly HistoricalCurrentState Current = new(true, Item.Group, Item.ItemRevision,
		Item.ReceiptRevision, AppliedAssignment, Guid.NewGuid(), true);

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void UndoCanRestorePreviousCategoryOrNullWhenEffectIsStillOwned(bool previousNull)
	{
		HistoricalReclassificationItem effect = Item with { PreviousCategoryId = previousNull ? null : Item.PreviousCategoryId };
		OrganizationEffectPolicy.Undo(effect, AppliedAssignment, Current.TaxonomyRevisionId,
			Current with { CategorySelectable = !previousNull }).Should().Be(new HistoricalDecision(HistoricalDecisionKind.Append));
	}

	[Fact]
	public void UndoKeepsLaterAssignmentEvenWhenCategoryCouldBeTheSame() =>
		OrganizationEffectPolicy.Undo(Item, AppliedAssignment, Current.TaxonomyRevisionId,
			Current with { AssignmentId = Guid.NewGuid() })
			.Should().Be(new HistoricalDecision(HistoricalDecisionKind.Conflict, "assignment-no-longer-owned"));

	[Theory]
	[InlineData("store")]
	[InlineData("description")]
	[InlineData("code")]
	[InlineData("item-revision")]
	[InlineData("receipt-revision")]
	public void UndoPreservesChangedSourceIncludingNormalizedCollisions(string change)
	{
		HistoricalCurrentState current = change switch
		{
			"store" => Current with { Group = Item.Group with { Store = "store" } },
			"description" => Current with { Group = Item.Group with { RawDescription = "Milk" } },
			"code" => Current with { Group = Item.Group with { ItemCode = "" } },
			"item-revision" => Current with { ItemRevision = Guid.NewGuid() },
			_ => Current with { ReceiptRevision = Guid.NewGuid() }
		};
		OrganizationEffectPolicy.Undo(Item, AppliedAssignment, Current.TaxonomyRevisionId, current)
			.Should().Be(new HistoricalDecision(HistoricalDecisionKind.Conflict, "source-changed"));
	}

	[Fact]
	public void UndoSkipsMissingOrEmptyAppliedAssignmentEvidence()
	{
		foreach (Guid? applied in new Guid?[] { null, Guid.Empty })
		{
			OrganizationEffectPolicy.Undo(Item, applied, Current.TaxonomyRevisionId, Current with { AssignmentId = applied })
				.Should().Be(new HistoricalDecision(HistoricalDecisionKind.Skip, "not-applied"));
		}
	}

	[Fact]
	public void UndoPreservesUnavailablePurchase() =>
		OrganizationEffectPolicy.Undo(Item, AppliedAssignment, Current.TaxonomyRevisionId, Current with { Available = false })
			.Should().Be(new HistoricalDecision(HistoricalDecisionKind.Skip, "source-unavailable"));

	[Fact]
	public void UndoRequiresCurrentTaxonomyAndSelectablePreviousCategory()
	{
		OrganizationEffectPolicy.Undo(Item, AppliedAssignment, Guid.NewGuid(), Current)
			.Should().Be(new HistoricalDecision(HistoricalDecisionKind.Conflict, "taxonomy-changed"));
		OrganizationEffectPolicy.Undo(Item, AppliedAssignment, Current.TaxonomyRevisionId, Current with { CategorySelectable = false })
			.Should().Be(new HistoricalDecision(HistoricalDecisionKind.Conflict, "previous-category-unavailable"));
	}

	private static PurchaseClassification Assignment(string source, Guid? category, string? resolution = null) =>
		new(Guid.NewGuid(), Guid.NewGuid(), 1, null, category, Guid.NewGuid(), DateTimeOffset.UtcNow,
			"actor", source, "reason", Guid.NewGuid(), [], LegacyResolution: resolution);
}
