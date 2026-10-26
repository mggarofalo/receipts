using Application.Models.Ynab;
using Application.Taxonomy;
using Domain;
using Domain.Core;
using Domain.Taxonomy;

namespace Application.Tests.Taxonomy;

public sealed class YnabReceiptMappingResolverTests
{
	private static readonly Guid Root = Guid.NewGuid();
	private static readonly Guid OldParent = Guid.NewGuid();
	private static readonly Guid Leaf = Guid.NewGuid();
	private static readonly Guid Revision = Guid.NewGuid();
	private static readonly TaxonomyNode[] Recorded =
	[
		new(Root, null, "Root", null, false),
		new(OldParent, Root, "Old parent", null, false),
		new(Leaf, OldParent, "Old leaf name", null, false),
	];

	private static ReceiptItem Item(string display = "New leaf name") => new(
		Guid.NewGuid(), null, "Item", 1, new Money(10), new Money(10), display, null)
	{
		CurrentClassification = new(Guid.NewGuid(), Leaf, Revision, Recorded),
	};

	private static YnabCategoryMappingDto Mapping(Guid categoryId, string budget, string target) =>
		new(Guid.NewGuid(), "stale name", target, "Target", "Group", budget, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
		{ ReceiptsCategoryId = categoryId };

	[Fact]
	public void RenamedItemUsesDirectStableIdentityInSelectedBudget()
	{
		ReceiptItem item = Item();
		var result = YnabReceiptMappingResolver.Resolve([item], "budget-a",
		[Mapping(Root, "budget-a", "root"), Mapping(Leaf, "budget-b", "wrong-budget"), Mapping(Leaf, "budget-a", "direct")]);
		Assert.Empty(result.Unmapped);
		Assert.Equal("direct", result.ItemTargets[YnabReceiptMappingResolver.ItemKey(item.Id)]);
	}

	[Fact]
	public void RecordedAncestorWinsAfterCurrentMoveAndDisplayRename()
	{
		ReceiptItem item = Item();
		var result = YnabReceiptMappingResolver.Resolve([item], "budget-a",
		[Mapping(Root, "budget-a", "root"), Mapping(OldParent, "budget-a", "old-parent")]);
		Assert.Empty(result.Unmapped);
		Assert.Equal("old-parent", result.ItemTargets[YnabReceiptMappingResolver.ItemKey(item.Id)]);
	}

	[Fact]
	public void ExplicitClearAndLegacyNameDoNotExport()
	{
		ReceiptItem cleared = Item("stale name");
		cleared.CurrentClassification = new(Guid.NewGuid(), null, Revision, []);
		YnabCategoryMappingDto legacy = new(Guid.NewGuid(), "stale name", "legacy", "Legacy", "Group",
			"budget-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
		var result = YnabReceiptMappingResolver.Resolve([cleared], "budget-a", [legacy, Mapping(Root, "budget-a", "root")]);
		Assert.Empty(result.ItemTargets);
		Assert.Single(result.Unmapped);
		Assert.Contains("Unassigned", result.Unmapped[0]);
	}
}
