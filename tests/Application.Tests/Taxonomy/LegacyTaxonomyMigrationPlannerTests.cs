using Application.Taxonomy;
using Domain.Taxonomy;
using FluentAssertions;

namespace Application.Tests.Taxonomy;

public sealed class LegacyTaxonomyMigrationPlannerTests
{
	private static readonly DateTimeOffset Boundary = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
	private static readonly LegacyDeletionState Present = new();
	private static readonly LegacyDeletionState Deleted = new(Boundary.AddDays(-1), "historical-user", Id(900));
	private static Guid Id(int number) => new(number, 0, 0, new byte[8]);
	private static LegacyCategoryEvidence Category(int id, string name, bool active = true, LegacyDeletionState? deletion = null)
		=> new(Id(id), name, null, active, deletion ?? Present);
	private static LegacySubcategoryEvidence Child(int id, int parent, string name, bool active = true, LegacyDeletionState? deletion = null)
		=> new(Id(id), Id(parent), name, null, active, deletion ?? Present);
	private static LegacyItemEvidence Item(int id, string? category, string? child = null, LegacyDeletionState? deletion = null)
		=> new(Id(id), Id(800), category, child, deletion ?? Present, Present, 12.34m, "USD");
	private static LegacyMigrationRequest Request(LegacyCategoryEvidence[]? categories = null, LegacySubcategoryEvidence[]? children = null, LegacyItemEvidence[]? items = null)
		=> new("frozen-dataset", Id(999), "initial_migration", "migration-admin", Boundary, categories ?? [], children ?? [], items ?? []);

	[Fact]
	public void ExactParentScopedPathsAndRootAssignmentsPreserveAllEvidence()
	{
		LegacyMigrationRequest request = Request([Category(1, "Grocery"), Category(2, "Household"), Category(3, "Uncategorized")],
			[Child(11, 1, "Dairy"), Child(12, 2, "Dairy")],
			[Item(101, "Grocery", "Dairy"), Item(102, "Household", "Dairy"), Item(103, "Grocery"), Item(104, "Uncategorized")]);
		LegacyMigrationPlan actual = LegacyTaxonomyMigrationPlanner.Plan(request);
		actual.CanCommit.Should().BeTrue();
		actual.Items.Should().OnlyContain(row => row.Kind == LegacyResolutionKind.ResolvedSuggestion);
		actual.Items[0].CategoryId.Should().NotBe(actual.Items[1].CategoryId!.Value);
		TaxonomyNode grocery = actual.Nodes.Single(row => row.Node.Name == "Grocery").Node;
		actual.Nodes.Single(row => row.Node.Id == actual.Items[0].CategoryId).Node.ParentId.Should().Be(grocery.Id);
		actual.Items[2].CategoryId.Should().Be(grocery.Id);
		actual.Nodes.Single(row => row.Node.Id == actual.Items[3].CategoryId).Node.Name.Should().Be("Uncategorized");
		actual.Items.Select(row => row.Evidence).Should().BeEquivalentTo(request.Items);
		actual.Counts.Totals.Should().Equal(new LegacyCurrencyTotal("USD", 49.36m));
	}

	[Fact]
	public void ObservedPathsDoNotInferRenameAndKeepCaseAndParentDistinctions()
	{
		LegacyMigrationPlan actual = LegacyTaxonomyMigrationPlanner.Plan(Request([Category(1, "Food")], items:
			[Item(101, "Grocery", "Dairy"), Item(102, "grocery", "Dairy"), Item(103, "Household", "Dairy"), Item(104, "Food", "Dairy")]));
		actual.Nodes.Should().HaveCount(8);
		actual.Items.Select(row => row.CategoryId).Distinct().Should().HaveCount(4);
		actual.Items.Should().OnlyContain(row => row.Kind == LegacyResolutionKind.ResolvedObservedPath);
		actual.Nodes.Where(row => row.ObservedOnly).Should().OnlyContain(row => !row.Node.Archived);
		actual.Nodes.Single(row => row.Node.Name == "Food").ObservedOnly.Should().BeFalse();
	}

	[Theory]
	[InlineData(null, null, LegacyResolutionKind.Unclassified)]
	[InlineData("", "", LegacyResolutionKind.Unclassified)]
	[InlineData(null, "Dairy", LegacyResolutionKind.Unresolved)]
	[InlineData("", "Dairy", LegacyResolutionKind.Unresolved)]
	[InlineData(" ", null, LegacyResolutionKind.Unresolved)]
	[InlineData(" Grocery", null, LegacyResolutionKind.Unresolved)]
	[InlineData("Grocery ", null, LegacyResolutionKind.Unresolved)]
	[InlineData("Grocery", " Dairy", LegacyResolutionKind.Unresolved)]
	[InlineData("Grocery", "", LegacyResolutionKind.ResolvedObservedPath)]
	public void NullEmptyAndDirtyNamesArePreservedWithoutSilentNormalization(string? category, string? child, LegacyResolutionKind expected)
	{
		LegacyMigrationPlan actual = LegacyTaxonomyMigrationPlanner.Plan(Request(items: [Item(101, category, child)]));
		actual.Items[0].Kind.Should().Be(expected);
		actual.Items[0].Evidence.Category.Should().Be(category);
		actual.Items[0].Evidence.Subcategory.Should().Be(child);
		actual.CanCommit.Should().BeTrue();
		if (expected != LegacyResolutionKind.ResolvedObservedPath) { actual.Nodes.Should().BeEmpty(); }
	}

	[Fact]
	public void DeletedDuplicatesRemainAmbiguousInsteadOfPreferringActiveIdentity()
	{
		LegacyMigrationPlan actual = LegacyTaxonomyMigrationPlanner.Plan(Request(
			[Category(1, "Grocery"), Category(2, "Grocery", deletion: Deleted), Category(3, "Food")],
			[Child(11, 3, "Dairy"), Child(12, 3, "Dairy", deletion: Deleted)],
			[Item(101, "Grocery"), Item(102, "Food", "Dairy")]));
		actual.CanCommit.Should().BeTrue();
		actual.Crosswalks.Should().HaveCount(5);
		actual.Nodes.Should().HaveCount(5);
		actual.Nodes.Count(row => row.Node.Archived).Should().Be(2);
		actual.Items.Should().OnlyContain(row => row.Kind == LegacyResolutionKind.Unresolved && row.CategoryId == null);
		actual.Items.Select(row => row.Exception).Should().Equal("ambiguous_category", "ambiguous_subcategory");
		actual.Counts.DuplicatePaths.Should().Be(2);
	}

	[Fact]
	public void DuplicateSelectableSiblingsBlockPreflightButRetainEverySource()
	{
		LegacyMigrationPlan actual = LegacyTaxonomyMigrationPlanner.Plan(Request([Category(1, "Food"), Category(2, "Food")], items: [Item(101, "Food")]));
		actual.CanCommit.Should().BeFalse();
		actual.Crosswalks.Should().HaveCount(2);
		actual.Nodes.Should().HaveCount(2);
		actual.Exceptions.Should().Contain(row => row.Code == "duplicate_selectable_siblings" && row.BlocksCommit);
		actual.Items[0].Kind.Should().Be(LegacyResolutionKind.Unresolved);
	}

	[Fact]
	public void CrossTableUuidCollisionHasDistinctStableTargetsAndArchivesDescendants()
	{
		LegacyMigrationPlan actual = LegacyTaxonomyMigrationPlanner.Plan(Request([Category(1, "Food", active: false)],
			[Child(1, 1, "Dairy")], [Item(101, "Food", "Dairy"), Item(102, "Food", "NewChild")]));
		actual.Crosswalks.Select(row => row.SourceId).Distinct().Should().ContainSingle();
		actual.Crosswalks.Select(row => row.TargetId).Distinct().Should().HaveCount(2);
		actual.Nodes.Should().OnlyContain(row => row.Node.Archived);
		actual.Items.Should().OnlyContain(row => row.CategoryId != null);
		actual.CanCommit.Should().BeTrue();
	}

	[Fact]
	public void TrashOnlyObservedPathsArchiveButAnyLiveEvidenceMakesThePathAvailable()
	{
		LegacyItemEvidence receiptTrash = Item(104, "Trash", "Dairy") with { ReceiptDeletion = Deleted };
		LegacyMigrationPlan actual = LegacyTaxonomyMigrationPlanner.Plan(Request(items:
			[Item(101, "Mixed", "Dairy", Deleted), Item(102, "Mixed", "Dairy"), Item(103, "Trash", "Dairy", Deleted), receiptTrash]));
		TaxonomyNode mixed = actual.Nodes.Single(row => row.Node.Name == "Mixed").Node;
		TaxonomyNode trash = actual.Nodes.Single(row => row.Node.Name == "Trash").Node;
		actual.Nodes.Where(row => row.Node.Id == mixed.Id || row.Node.ParentId == mixed.Id).Should().OnlyContain(row => !row.Node.Archived);
		actual.Nodes.Where(row => row.Node.Id == trash.Id || row.Node.ParentId == trash.Id).Should().OnlyContain(row => row.Node.Archived);
		actual.Counts.TrashedItems.Should().Be(3);
		actual.Counts.Items.Should().Be(4);
		actual.Items.Single(row => row.Evidence.Id == receiptTrash.Id).Evidence.ReceiptDeletion.Should().Be(Deleted);
	}

	[Fact]
	public void OrphanSourcesAndInvalidScalarsBlockWithoutLosingCrosswalkRawValues()
	{
		LegacyCategoryEvidence dirty = Category(2, " Food ") with { Definition = "original" };
		LegacyMigrationPlan actual = LegacyTaxonomyMigrationPlanner.Plan(Request([Category(1, "Food"), dirty],
			[Child(11, 404, "Orphan"), Child(12, 2, "Child")], [Item(101, " Food ")]));
		actual.CanCommit.Should().BeFalse();
		actual.Crosswalks.Should().HaveCount(4);
		actual.Crosswalks.Single(row => row.SourceId == dirty.Id).Category.Should().Be(dirty);
		actual.Crosswalks.Single(row => row.SourceId == Id(11)).Exception.Should().Be("orphan_suggestion_parent");
		actual.Nodes.Should().ContainSingle();
		actual.Items[0].Kind.Should().Be(LegacyResolutionKind.Unresolved);
	}

	[Theory]
	[InlineData("nul")]
	[InlineData("long-name")]
	[InlineData("long-definition")]
	public void ScalarBoundsAreExplicitExceptionsInsteadOfTruncation(string scenario)
	{
		LegacyCategoryEvidence source = Category(1, scenario == "nul" ? "Food\0" : scenario == "long-name" ? new string('x', 201) : "Food")
			with
		{ Definition = scenario == "long-definition" ? new string('x', 4001) : null };
		LegacyMigrationPlan actual = LegacyTaxonomyMigrationPlanner.Plan(Request([source]));
		actual.CanCommit.Should().BeFalse();
		actual.Crosswalks[0].Category.Should().Be(source);
		actual.Nodes.Should().BeEmpty();
	}

	[Fact]
	public void ProductReferencesNeverBecomeCategoryEvidenceAndFinancialAndTrashEvidenceIsRetained()
	{
		LegacyItemEvidence source = Item(101, null, deletion: Deleted) with { ProductId = Id(700), ProductLinkException = "missing product named Milk", Amount = -1.25m, Currency = "EUR" };
		LegacyMigrationPlan actual = LegacyTaxonomyMigrationPlanner.Plan(Request(items: [source, Item(102, "Food")]));
		actual.Items[0].Kind.Should().Be(LegacyResolutionKind.Unclassified);
		actual.Items[0].Evidence.Should().Be(source);
		actual.Nodes.Should().ContainSingle(row => row.Node.Name == "Food");
		actual.Exceptions.Should().ContainSingle(row => row.Code == "product_link_exception" && row.ItemId == source.Id);
		actual.Counts.DirtyProductLinks.Should().Be(1);
		actual.Counts.Totals.Should().Equal(new LegacyCurrencyTotal("EUR", -1.25m), new LegacyCurrencyTotal("USD", 12.34m));
	}

	[Fact]
	public void InputPermutationAndExactOperationRetryReturnStableIdentitiesAndOutcomes()
	{
		LegacyMigrationRequest request = Request([Category(2, "Other"), Category(1, "Food")], [Child(12, 2, "Dairy"), Child(11, 1, "Dairy")],
			[Item(102, "Absent", "New"), Item(101, "Food", "Dairy")]);
		LegacyMigrationPlan expected = LegacyTaxonomyMigrationPlanner.Plan(request);
		LegacyMigrationRequest reordered = request with { Categories = request.Categories.Reverse().ToArray(), Subcategories = request.Subcategories.Reverse().ToArray(), Items = request.Items.Reverse().ToArray() };
		LegacyTaxonomyMigrationPlanner.Plan(reordered).Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
		LegacyTaxonomyMigrationPlanner.Plan(reordered, expected).Should().BeSameAs(expected);
		LegacyMigrationPlan anotherOperation = LegacyTaxonomyMigrationPlanner.Plan(request with { OperationId = Id(998) });
		anotherOperation.Nodes.Should().BeEquivalentTo(expected.Nodes);
		anotherOperation.Items[0].AssignmentId.Should().NotBe(expected.Items[0].AssignmentId);
		anotherOperation.RevisionId.Should().NotBe(expected.RevisionId);
	}

	[Theory]
	[InlineData("dataset")]
	[InlineData("kind")]
	[InlineData("actor")]
	[InlineData("boundary")]
	[InlineData("evidence")]
	[InlineData("operation")]
	public void ChangedEvidenceOrProvenanceCannotReuseAnOperation(string changed)
	{
		LegacyMigrationRequest request = Request(items: [Item(101, "Food")]);
		LegacyMigrationPlan previous = LegacyTaxonomyMigrationPlanner.Plan(request);
		LegacyMigrationRequest mutation = changed switch
		{
			"dataset" => request with { SourceDataset = "another-source" },
			"kind" => request with { OperationKind = "old_backup_import" },
			"actor" => request with { Actor = "another-actor" },
			"boundary" => request with { ObservedAt = Boundary.AddSeconds(1) },
			"operation" => request with { OperationId = Id(998) },
			_ => request with { Items = [Item(101, "Food") with { Subcategory = "" }] }
		};
		Action actual = () => LegacyTaxonomyMigrationPlanner.Plan(mutation, previous);
		actual.Should().Throw<LegacyMigrationReplayException>();
	}

	[Fact]
	public void UnicodeScalarBoundAcceptsAstralNamesWithoutCountingSurrogatePairsTwice()
	{
		string name = string.Concat(Enumerable.Repeat("🍎", 200));
		LegacyMigrationPlan actual = LegacyTaxonomyMigrationPlanner.Plan(Request([Category(1, name)], items: [Item(101, name)]));
		actual.CanCommit.Should().BeTrue();
		actual.Items[0].Kind.Should().Be(LegacyResolutionKind.ResolvedSuggestion);
		actual.Nodes[0].Node.Name.Should().Be(name);
	}

	[Fact]
	public void DuplicateSourceIdsWithinATableAreRejectedBeforePlanning()
	{
		Action actual = () => LegacyTaxonomyMigrationPlanner.Plan(Request([Category(1, "Food"), Category(1, "Other")]));
		actual.Should().Throw<ArgumentException>().WithMessage("*unique within each source table*");
	}

	[Fact]
	public void DatasetNamespacesAndFramedPathSegmentsPreventAccidentalIdentityReuse()
	{
		LegacyMigrationRequest request = Request(items: [Item(101, "A/B", "C"), Item(102, "A", "B/C")]);
		LegacyMigrationPlan expected = LegacyTaxonomyMigrationPlanner.Plan(request);
		expected.Items.Select(row => row.CategoryId).Distinct().Should().HaveCount(2);
		LegacyMigrationPlan actual = LegacyTaxonomyMigrationPlanner.Plan(request with { SourceDataset = "another-frozen-dataset" });
		actual.Nodes.Select(row => row.Node.Id).Should().NotIntersectWith(expected.Nodes.Select(row => row.Node.Id));
	}
}
