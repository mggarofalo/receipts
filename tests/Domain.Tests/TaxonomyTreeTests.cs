using Domain.Taxonomy;
using FluentAssertions;

namespace Domain.Tests;

public sealed class TaxonomyTreeTests
{
	private static readonly Guid Root = Guid.NewGuid();
	private static readonly Guid Child = Guid.NewGuid();
	private static readonly Guid Leaf = Guid.NewGuid();
	private static TaxonomyNode[] Tree => [new(Root, null, "Food", null, false), new(Child, Root, "Dairy", null, false), new(Leaf, Child, "Milk", null, false)];

	[Fact]
	public void NamesAndDefinitionsRejectNulCharacters()
	{
		Action name = () => TaxonomyTree.Apply([], [new("create", Root, Name: "Food\0")]);
		Action definition = () => TaxonomyTree.Apply([], [new("create", Root, Name: "Food", Definition: "Contains\0NUL")]);
		name.Should().Throw<TaxonomyValidationException>().WithMessage("*NUL*");
		definition.Should().Throw<TaxonomyValidationException>().WithMessage("*NUL*");
	}

	[Fact]
	public void NamesAndDefinitionsHaveExplicitUnicodeScalarBounds()
	{
		string astralName = string.Concat(Enumerable.Repeat("🍎", 200));
		TaxonomyTree.Apply([], [new("create", Root, Name: astralName, Definition: new string('a', 4000))]).Should().ContainSingle();
		Action longName = () => TaxonomyTree.Apply([], [new("create", Root, Name: astralName + "x")]);
		Action longDefinition = () => TaxonomyTree.Apply([], [new("create", Root, Name: "Food", Definition: new string('a', 4001))]);
		longName.Should().Throw<TaxonomyValidationException>();
		longDefinition.Should().Throw<TaxonomyValidationException>();
	}

	[Fact]
	public void MoveAndRenamePreserveOriginalDeepPathAndStableIdentity()
	{
		TaxonomyNode[] original = Tree;
		Guid other = Guid.NewGuid();
		IReadOnlyList<TaxonomyNode> actual = TaxonomyTree.Apply(original, [new("create", other, Name: "Chilled"), new("move", Child, other), new("rename", Root, Name: "Groceries")]);
		actual.Single(node => node.Id == Child).ParentId.Should().Be(other);
		actual.Single(node => node.Id == Leaf).ParentId.Should().Be(Child);
		original.Single(node => node.Id == Root).Name.Should().Be("Food");
		original.Single(node => node.Id == Child).ParentId.Should().Be(Root);
	}

	[Theory]
	[InlineData("move", "cycle")]
	[InlineData("create", "duplicate")]
	[InlineData("create", "missing")]
	[InlineData("rename", "space")]
	public void InvalidFinalTreesReject(string kind, string scenario)
	{
		TaxonomyEdit edit = scenario switch
		{
			"cycle" => new(kind, Root, Leaf),
			"duplicate" => new(kind, Guid.NewGuid(), Root, "Dairy"),
			"missing" => new(kind, Guid.NewGuid(), Guid.NewGuid(), "Absent"),
			_ => new(kind, Root, Name: " Food")
		};
		Action actual = () => TaxonomyTree.Apply(Tree, [edit]);
		actual.Should().Throw<TaxonomyValidationException>();
	}

	[Fact]
	public void ArchiveIncludesWholeSubtreeAndChildRestoreRequiresActiveAncestors()
	{
		IReadOnlyList<TaxonomyNode> archived = TaxonomyTree.Apply(Tree, [new("archive", Root)]);
		archived.Should().OnlyContain(node => node.Archived);
		Action actual = () => TaxonomyTree.Apply(archived, [new("restore", Leaf)]);
		actual.Should().Throw<TaxonomyValidationException>();
		TaxonomyTree.Apply(archived, [new("restore", Root), new("restore", Child), new("restore", Leaf)]).Should().BeEquivalentTo(Tree);
	}

	[Fact]
	public void ExactNamesAllowCaseDifferencesAndSameNameUnderDifferentParentsAndArchivedSiblings()
	{
		Guid food = Guid.NewGuid();
		IReadOnlyList<TaxonomyNode> actual = TaxonomyTree.Apply(Tree, [new("create", food, Name: "food"),
			new("create", Guid.NewGuid(), food, "Dairy"), new("archive", Child), new("create", Guid.NewGuid(), Root, "Dairy")]);
		actual.Should().HaveCount(6);
	}

	[Fact]
	public void CreatingBelowAChildIsRefused()
	{
		Guid dairy = Guid.NewGuid();
		TaxonomyNode[] tree = [new(Root, null, "Grocery", null, false), new(dairy, Root, "Dairy", null, false)];
		Action actual = () => TaxonomyTree.Apply(tree, [new("create", Guid.NewGuid(), dairy, "Whole Milk")]);
		actual.Should().Throw<TaxonomyValidationException>().WithMessage("*at most 2 levels*");
	}

	[Fact]
	public void MovingAParentBelowARootIsRefusedBecauseItsChildrenWouldBeAThirdLevel()
	{
		Guid dairy = Guid.NewGuid();
		TaxonomyNode[] tree = [new(Root, null, "Grocery", null, false), new(dairy, null, "Dairy", null, false),
			new(Leaf, dairy, "Milk", null, false)];
		Action actual = () => TaxonomyTree.Apply(tree, [new("move", dairy, Root)]);
		actual.Should().Throw<TaxonomyValidationException>().WithMessage("*at most 2 levels*");
		TaxonomyTree.Apply(tree, [new("move", Leaf, Root), new("move", dairy, Root)])
			.Single(node => node.Id == dairy).ParentId.Should().Be(Root);
	}

	[Fact]
	public void IdenticalEditsProduceEqualTree()
	{
		TaxonomyTree.Apply(Tree, [new("rename", Root, Name: "Food"), new("move", Child, Root)]).Should().BeEquivalentTo(Tree);
	}
}
