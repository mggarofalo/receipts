using Application.Models.Ynab;
using Application.Taxonomy;
using Domain;
using Domain.Core;
using Domain.Envelopes;
using Domain.Taxonomy;
using FluentAssertions;

namespace Application.Tests.Taxonomy;

public sealed class YnabReceiptMappingResolverTests
{
	private static readonly Guid Groceries = Guid.NewGuid();
	private static readonly Guid Household = Guid.NewGuid();
	private static readonly Guid Root = Guid.NewGuid();
	private static readonly Guid Leaf = Guid.NewGuid();
	private static readonly Guid Revision = Guid.NewGuid();
	private static readonly Dictionary<Guid, string> Names = new() { [Groceries] = "Groceries", [Household] = "Household" };

	private static ReceiptItem Item(Guid? envelopeId, EnvelopeSource source = EnvelopeSource.Default, Guid? categoryId = null)
		=> new(Guid.NewGuid(), null, "Item", 1, new Money(10), new Money(10), "Display", null)
		{
			EnvelopeId = envelopeId,
			EnvelopeSource = source,
			CurrentClassification = categoryId is Guid id
				? new(Guid.NewGuid(), id, Revision, [new(Root, null, "Root", null, false), new(Leaf, Root, "Leaf", null, false)])
				: null,
		};

	private static YnabEnvelopeMappingDto Mapping(Guid envelopeId, string budget, string target) =>
		new(Guid.NewGuid(), envelopeId, Names.GetValueOrDefault(envelopeId) ?? "Envelope", target, "Target", "Group", budget,
			DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

	private static TaxonomyRevision CurrentRevision() => new(Revision, 1, null, DateTimeOffset.UtcNow, "test", "test",
		Guid.NewGuid(), [new(Root, null, "Root", null, false), new(Leaf, Root, "Leaf", null, false)], 0);

	[Fact]
	public void Resolve_EnvelopeMappedInSelectedBudget_UsesThatBudgetsCategory()
	{
		// Arrange
		ReceiptItem item = Item(Groceries);

		// Act
		(Dictionary<string, string> targets, List<string> unmapped) = YnabReceiptMappingResolver.Resolve([item], "budget-a",
			[Mapping(Groceries, "budget-b", "wrong-budget"), Mapping(Groceries, "budget-a", "direct")], Names);

		// Assert
		unmapped.Should().BeEmpty();
		targets[YnabReceiptMappingResolver.ItemKey(item.Id)].Should().Be("direct");
	}

	[Fact]
	public void Resolve_ItemWithoutEnvelope_IsUnresolved()
	{
		// Arrange
		ReceiptItem item = Item(null);

		// Act
		(Dictionary<string, string> targets, List<string> unmapped) =
			YnabReceiptMappingResolver.Resolve([item], "budget-a", [Mapping(Groceries, "budget-a", "x")], Names);

		// Assert
		targets.Should().BeEmpty();
		unmapped.Should().ContainSingle().Which.Should().Contain("Unresolved").And.Contain(item.Id.ToString("D"));
	}

	[Fact]
	public void Resolve_EnvelopeMappedOnlyInAnotherBudget_IsUnmappedByEnvelopeName()
	{
		// Arrange
		ReceiptItem item = Item(Household);

		// Act
		(Dictionary<string, string> targets, List<string> unmapped) =
			YnabReceiptMappingResolver.Resolve([item], "budget-a", [Mapping(Household, "budget-b", "x")], Names);

		// Assert
		targets.Should().BeEmpty();
		unmapped.Should().ContainSingle().Which.Should().StartWith("Household").And.Contain("Unmapped");
	}

	[Fact]
	public void Resolve_UnknownEnvelopeName_FallsBackToEnvelopeId()
	{
		// Arrange
		Guid unknown = Guid.NewGuid();
		ReceiptItem item = Item(unknown);

		// Act
		(_, List<string> unmapped) = YnabReceiptMappingResolver.Resolve([item], "budget-a", [], Names);

		// Assert
		unmapped.Should().ContainSingle().Which.Should().StartWith(unknown.ToString("D"));
	}

	[Fact]
	public void ResolveCurrent_DefaultSourcedItem_FollowsCurrentItemTypeDefault()
	{
		// Arrange
		ReceiptItem item = Item(Groceries, EnvelopeSource.Default, Leaf);
		Dictionary<Guid, Guid> defaults = new() { [Root] = Household };

		// Act
		(Dictionary<string, string> targets, List<string> unmapped) = YnabReceiptMappingResolver.ResolveCurrent([item], "budget-a",
			[Mapping(Groceries, "budget-a", "groceries"), Mapping(Household, "budget-a", "household")], Names, CurrentRevision(), defaults);

		// Assert
		unmapped.Should().BeEmpty();
		targets[YnabReceiptMappingResolver.ItemKey(item.Id)].Should().Be("household");
	}

	[Theory]
	[InlineData(EnvelopeSource.Item)]
	[InlineData(EnvelopeSource.Receipt)]
	[InlineData(EnvelopeSource.Historical)]
	public void ResolveCurrent_NonDefaultSourcedItem_KeepsRecordedEnvelope(EnvelopeSource source)
	{
		// Arrange
		ReceiptItem item = Item(Groceries, source, Leaf);
		Dictionary<Guid, Guid> defaults = new() { [Leaf] = Household };

		// Act
		(Dictionary<string, string> targets, _) = YnabReceiptMappingResolver.ResolveCurrent([item], "budget-a",
			[Mapping(Groceries, "budget-a", "groceries"), Mapping(Household, "budget-a", "household")], Names, CurrentRevision(), defaults);

		// Assert
		targets[YnabReceiptMappingResolver.ItemKey(item.Id)].Should().Be("groceries");
	}

	[Fact]
	public void ResolveCurrent_DefaultRemoved_DefaultSourcedItemBecomesUnresolved()
	{
		// Arrange
		ReceiptItem item = Item(Groceries, EnvelopeSource.Default, Leaf);

		// Act
		(Dictionary<string, string> targets, List<string> unmapped) = YnabReceiptMappingResolver.ResolveCurrent([item], "budget-a",
			[Mapping(Groceries, "budget-a", "groceries")], Names, CurrentRevision(), new Dictionary<Guid, Guid>());

		// Assert
		targets.Should().BeEmpty();
		unmapped.Should().ContainSingle().Which.Should().Contain("Unresolved");
	}
}
