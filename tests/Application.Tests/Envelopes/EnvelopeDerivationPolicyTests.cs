using Application.Envelopes;
using Domain.Envelopes;
using Domain.Taxonomy;
using FluentAssertions;

namespace Application.Tests.Envelopes;

public class EnvelopeDerivationPolicyTests
{
	private static readonly Guid Override = Guid.NewGuid();
	private static readonly Guid PaidFrom = Guid.NewGuid();
	private static readonly Guid ItemTypeDefault = Guid.NewGuid();
	private static readonly Guid Recorded = Guid.NewGuid();

	[Fact]
	public void ForNewItem_OverrideWinsOverReceiptAndDefault()
	{
		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.ForNewItem(Override, PaidFrom, ItemTypeDefault);

		// Assert
		actual.Should().Be(new EnvelopeAssignment(Override, EnvelopeSource.Item));
	}

	[Fact]
	public void ForNewItem_ReceiptPaidFromWinsOverDefault()
	{
		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.ForNewItem(null, PaidFrom, ItemTypeDefault);

		// Assert
		actual.Should().Be(new EnvelopeAssignment(PaidFrom, EnvelopeSource.Receipt));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void ForNewItem_NoChoices_IsDefaultSourcedEvenWhenUnresolved(bool hasDefault)
	{
		// Arrange
		Guid? itemTypeDefault = hasDefault ? ItemTypeDefault : null;

		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.ForNewItem(null, null, itemTypeDefault);

		// Assert
		actual.Should().Be(new EnvelopeAssignment(itemTypeDefault, EnvelopeSource.Default));
	}

	[Theory]
	[InlineData(EnvelopeSource.Default)]
	[InlineData(EnvelopeSource.Receipt)]
	[InlineData(EnvelopeSource.Historical)]
	public void ForEditedItem_WithoutOverride_KeepsStoredAssignment(EnvelopeSource source)
	{
		// Arrange
		EnvelopeAssignment stored = new(Recorded, source);

		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.ForEditedItem(stored, null);

		// Assert
		actual.Should().Be(stored);
	}

	[Fact]
	public void ForEditedItem_WithOverride_RecordsItemSource()
	{
		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.ForEditedItem(new(Recorded, EnvelopeSource.Historical), Override);

		// Assert
		actual.Should().Be(new EnvelopeAssignment(Override, EnvelopeSource.Item));
	}

	[Theory]
	[InlineData(EnvelopeSource.Default)]
	[InlineData(EnvelopeSource.Receipt)]
	[InlineData(EnvelopeSource.Item)]
	[InlineData(EnvelopeSource.Historical)]
	public void AfterOverrideChange_OverrideChosen_RecordsItemSourceOverAnySource(EnvelopeSource source)
	{
		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.AfterOverrideChange(new(Recorded, source), Override, PaidFrom, ItemTypeDefault);

		// Assert
		actual.Should().Be(new EnvelopeAssignment(Override, EnvelopeSource.Item));
	}

	[Fact]
	public void AfterOverrideChange_ClearedOverride_ReturnsToReceiptPaidFrom()
	{
		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.AfterOverrideChange(new(Override, EnvelopeSource.Item), null, PaidFrom, ItemTypeDefault);

		// Assert
		actual.Should().Be(new EnvelopeAssignment(PaidFrom, EnvelopeSource.Receipt));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void AfterOverrideChange_ClearedOverrideWithoutPaidFrom_ReturnsToItemTypeDefault(bool hasDefault)
	{
		// Arrange
		Guid? itemTypeDefault = hasDefault ? ItemTypeDefault : null;

		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.AfterOverrideChange(new(Override, EnvelopeSource.Item), null, null, itemTypeDefault);

		// Assert
		actual.Should().Be(new EnvelopeAssignment(itemTypeDefault, EnvelopeSource.Default));
	}

	[Theory]
	[InlineData(EnvelopeSource.Default)]
	[InlineData(EnvelopeSource.Receipt)]
	[InlineData(EnvelopeSource.Historical)]
	public void AfterOverrideChange_NoOverrideOnNonOverriddenItem_KeepsStoredAssignment(EnvelopeSource source)
	{
		// Arrange
		EnvelopeAssignment stored = new(Recorded, source);

		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.AfterOverrideChange(stored, null, PaidFrom, ItemTypeDefault);

		// Assert
		actual.Should().Be(stored);
	}

	[Theory]
	[InlineData(EnvelopeSource.Default, true, true)]
	[InlineData(EnvelopeSource.Default, false, true)]
	[InlineData(EnvelopeSource.Receipt, true, false)]
	[InlineData(EnvelopeSource.Item, true, false)]
	[InlineData(EnvelopeSource.Historical, true, false)]
	[InlineData(EnvelopeSource.Receipt, false, true)]
	[InlineData(EnvelopeSource.Item, false, true)]
	[InlineData(EnvelopeSource.Historical, false, true)]
	public void FollowsItemType_OnlyDefaultSourcedOrUnresolved(EnvelopeSource source, bool hasEnvelope, bool expected)
	{
		// Act
		bool actual = EnvelopeDerivationPolicy.FollowsItemType(new(hasEnvelope ? Recorded : null, source));

		// Assert
		actual.Should().Be(expected);
	}

	[Theory]
	[InlineData(EnvelopeSource.Receipt)]
	[InlineData(EnvelopeSource.Item)]
	[InlineData(EnvelopeSource.Historical)]
	public void AfterItemTypeChange_ChosenOrHistoricalEnvelope_IsPreserved(EnvelopeSource source)
	{
		// Arrange
		EnvelopeAssignment stored = new(Recorded, source);

		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.AfterItemTypeChange(stored, ItemTypeDefault);

		// Assert
		actual.Should().Be(stored);
	}

	[Theory]
	[InlineData(EnvelopeSource.Default, true)]
	[InlineData(EnvelopeSource.Default, false)]
	[InlineData(EnvelopeSource.Historical, false)]
	[InlineData(EnvelopeSource.Receipt, false)]
	public void AfterItemTypeChange_DefaultSourcedOrUnresolved_FollowsNewDefault(EnvelopeSource source, bool hasEnvelope)
	{
		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.AfterItemTypeChange(
			new(hasEnvelope ? Recorded : null, source), ItemTypeDefault);

		// Assert
		actual.Should().Be(new EnvelopeAssignment(ItemTypeDefault, EnvelopeSource.Default));
	}

	[Fact]
	public void AfterItemTypeChange_NewTypeWithoutDefault_BecomesUnresolvedDefault()
	{
		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.AfterItemTypeChange(new(Recorded, EnvelopeSource.Default), null);

		// Assert
		actual.Should().Be(new EnvelopeAssignment(null, EnvelopeSource.Default));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void AfterPaidFromChange_ItemOverride_IsNeverReplaced(bool setPaidFrom)
	{
		// Arrange
		EnvelopeAssignment stored = new(Override, EnvelopeSource.Item);

		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.AfterPaidFromChange(stored, setPaidFrom ? PaidFrom : null, ItemTypeDefault);

		// Assert
		actual.Should().Be(stored);
	}

	[Theory]
	[InlineData(EnvelopeSource.Default)]
	[InlineData(EnvelopeSource.Receipt)]
	[InlineData(EnvelopeSource.Historical)]
	public void AfterPaidFromChange_Set_AppliesReceiptEnvelopeToNonOverrides(EnvelopeSource source)
	{
		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.AfterPaidFromChange(new(Recorded, source), PaidFrom, ItemTypeDefault);

		// Assert
		actual.Should().Be(new EnvelopeAssignment(PaidFrom, EnvelopeSource.Receipt));
	}

	[Fact]
	public void AfterPaidFromChange_Cleared_ReceiptSourcedReturnsToItemTypeDefault()
	{
		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.AfterPaidFromChange(new(PaidFrom, EnvelopeSource.Receipt), null, ItemTypeDefault);

		// Assert
		actual.Should().Be(new EnvelopeAssignment(ItemTypeDefault, EnvelopeSource.Default));
	}

	[Theory]
	[InlineData(EnvelopeSource.Default)]
	[InlineData(EnvelopeSource.Historical)]
	public void AfterPaidFromChange_Cleared_NonReceiptSourcedIsKept(EnvelopeSource source)
	{
		// Arrange
		EnvelopeAssignment stored = new(Recorded, source);

		// Act
		EnvelopeAssignment actual = EnvelopeDerivationPolicy.AfterPaidFromChange(stored, null, ItemTypeDefault);

		// Assert
		actual.Should().Be(stored);
	}

	[Fact]
	public void ResolveDefault_Path_UsesNearestMappedAncestorOrSelf()
	{
		// Arrange
		TaxonomyNode root = Node(null), middle = Node(root.Id), leaf = Node(middle.Id);
		Guid rootEnvelope = Guid.NewGuid(), middleEnvelope = Guid.NewGuid();
		Dictionary<Guid, Guid> defaults = new() { [root.Id] = rootEnvelope, [middle.Id] = middleEnvelope };

		// Act
		Guid? actual = EnvelopeDerivationPolicy.ResolveDefault([root, middle, leaf], defaults);

		// Assert
		actual.Should().Be(middleEnvelope);
	}

	[Fact]
	public void ResolveDefault_Path_SelfDefaultBeatsAncestor()
	{
		// Arrange
		TaxonomyNode root = Node(null), leaf = Node(root.Id);
		Guid leafEnvelope = Guid.NewGuid();
		Dictionary<Guid, Guid> defaults = new() { [root.Id] = Guid.NewGuid(), [leaf.Id] = leafEnvelope };

		// Act
		Guid? actual = EnvelopeDerivationPolicy.ResolveDefault([root, leaf], defaults);

		// Assert
		actual.Should().Be(leafEnvelope);
	}

	[Fact]
	public void ResolveDefault_Path_NoMappedNode_IsNull()
	{
		// Act
		Guid? actual = EnvelopeDerivationPolicy.ResolveDefault([Node(null)], new Dictionary<Guid, Guid> { [Guid.NewGuid()] = Guid.NewGuid() });

		// Assert
		actual.Should().BeNull();
	}

	[Fact]
	public void ResolveDefault_Revision_WalksParentsToNearestDefault()
	{
		// Arrange
		TaxonomyNode root = Node(null), middle = Node(root.Id), leaf = Node(middle.Id);
		Guid expected = Guid.NewGuid();

		// Act
		Guid? actual = EnvelopeDerivationPolicy.ResolveDefault(leaf.Id, Nodes(root, middle, leaf),
			new Dictionary<Guid, Guid> { [root.Id] = expected });

		// Assert
		actual.Should().Be(expected);
	}

	[Fact]
	public void ResolveDefault_Revision_NullCategory_IsNull()
	{
		// Arrange
		TaxonomyNode root = Node(null);

		// Act
		Guid? actual = EnvelopeDerivationPolicy.ResolveDefault(null, Nodes(root), new Dictionary<Guid, Guid> { [root.Id] = Guid.NewGuid() });

		// Assert
		actual.Should().BeNull();
	}

	[Fact]
	public void ResolveDefault_Revision_CategoryMissingFromRevision_IsNull()
	{
		// Arrange
		TaxonomyNode root = Node(null);

		// Act
		Guid? actual = EnvelopeDerivationPolicy.ResolveDefault(Guid.NewGuid(), Nodes(root),
			new Dictionary<Guid, Guid> { [root.Id] = Guid.NewGuid() });

		// Assert
		actual.Should().BeNull();
	}

	[Fact]
	public void ResolveDefault_Revision_MissingAncestorStopsTheWalkWithoutThrowing()
	{
		// Arrange
		Guid missingParent = Guid.NewGuid();
		TaxonomyNode orphan = Node(missingParent);

		// Act
		Guid? actual = EnvelopeDerivationPolicy.ResolveDefault(orphan.Id, Nodes(orphan),
			new Dictionary<Guid, Guid> { [missingParent] = Guid.NewGuid() });

		// Assert
		actual.Should().BeNull();
	}

	[Fact]
	public void ResolveDefault_Revision_CyclicAncestryTerminates()
	{
		// Arrange
		Guid a = Guid.NewGuid(), b = Guid.NewGuid();
		TaxonomyNode nodeA = new(a, b, "A", null, false);
		TaxonomyNode nodeB = new(b, a, "B", null, false);

		// Act
		Func<Guid?> act = () => EnvelopeDerivationPolicy.ResolveDefault(a, Nodes(nodeA, nodeB), new Dictionary<Guid, Guid>());

		// Assert
		act.Should().NotThrow().Which.Should().BeNull();
	}

	private static TaxonomyNode Node(Guid? parentId) => new(Guid.NewGuid(), parentId, "Node", null, false);

	private static Dictionary<Guid, TaxonomyNode> Nodes(params TaxonomyNode[] nodes) => nodes.ToDictionary(node => node.Id);
}
