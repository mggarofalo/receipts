using Application.Taxonomy;
using Domain.Taxonomy;

namespace Application.Tests.Taxonomy;

public sealed class ExternalCategoryMappingPolicyTests
{
	private static readonly Guid Root = Guid.NewGuid();
	private static readonly Guid Parent = Guid.NewGuid();
	private static readonly Guid Leaf = Guid.NewGuid();
	private static readonly Guid Revision = Guid.NewGuid();
	private static readonly ExternalCategoryScope Scope = new("ynab", "budget-a");
	private static readonly TaxonomyNode[] Path = [new(Root, null, "Root", null, false),
		new(Parent, Root, "Parent", null, false), new(Leaf, Parent, "Leaf", null, false)];
	private static CurrentClassification Assignment => new(Guid.NewGuid(), Leaf, Revision, Path);
	private static ExternalCategoryMapping Mapping(Guid category, string target = "external") =>
		new(Guid.NewGuid(), category, Scope, target);

	[Fact]
	public void DirectMappingWinsOverAncestorsAndOtherBudgets()
	{
		ExternalCategoryMapping direct = Mapping(Leaf, "direct");
		ExternalCategoryMappingResolution actual = ExternalCategoryMappingPolicy.ResolveRecorded(Assignment, Scope,
			[Mapping(Root), Mapping(Parent), direct, Mapping(Leaf) with { Scope = new("ynab", "budget-b") }]);
		Assert.Equal(ExternalCategoryMappingDisposition.Mapped, actual.Disposition);
		Assert.Equal(direct, actual.Mapping);
		Assert.Equal(0, actual.AncestorDistance);
		Assert.Equal(Revision, actual.TaxonomyRevisionId);
		Assert.Equal(ExternalCategoryAncestry.RecordedClassification, actual.Ancestry);
	}

	[Fact]
	public void NearestAncestorMappingIsExposedWithProvenance()
	{
		ExternalCategoryMapping parent = Mapping(Parent);
		ExternalCategoryMappingResolution actual = ExternalCategoryMappingPolicy.ResolveRecorded(Assignment, Scope,
			[Mapping(Root), parent]);
		Assert.Equal(parent, actual.Mapping);
		Assert.Equal(1, actual.AncestorDistance);
		Assert.Equal(Leaf, actual.CategoryId);
	}

	[Fact]
	public void MoveChangesCurrentPreviewButLeavesRecordedResolutionUnchanged()
	{
		Guid newParent = Guid.NewGuid();
		Guid currentRevision = Guid.NewGuid();
		TaxonomyNode[] current = [Path[0], new(newParent, Root, "New", null, false),
			Path[2] with { ParentId = newParent, Name = "Renamed" }];
		ExternalCategoryMapping[] mappings = [Mapping(Parent, "old"), Mapping(newParent, "new")];
		ExternalCategoryMappingResolution recorded = ExternalCategoryMappingPolicy.ResolveRecorded(Assignment, Scope, mappings);
		ExternalCategoryMappingResolution preview = ExternalCategoryMappingPolicy.ResolveCurrent(Leaf, currentRevision, current, Scope, mappings);
		Assert.Equal("old", recorded.Mapping!.ExternalCategoryId);
		Assert.Equal("new", preview.Mapping!.ExternalCategoryId);
		Assert.Equal(ExternalCategoryAncestry.CurrentTaxonomy, preview.Ancestry);
		Assert.Equal(currentRevision, preview.TaxonomyRevisionId);
	}

	[Fact]
	public void DuplicateDirectMappingsRequireReviewInsteadOfFallingBack()
	{
		ExternalCategoryMappingResolution actual = ExternalCategoryMappingPolicy.ResolveRecorded(Assignment, Scope,
			[Mapping(Leaf), Mapping(Leaf), Mapping(Parent)]);
		Assert.Equal(ExternalCategoryMappingDisposition.AmbiguousMapping, actual.Disposition);
		Assert.Null(actual.Mapping);
	}

	[Fact]
	public void ExplicitUnclassificationDoesNotInheritOrUseLegacyNames()
	{
		ExternalCategoryMappingResolution actual = ExternalCategoryMappingPolicy.ResolveRecorded(
			Assignment with { CategoryId = null, RecordedPath = [] }, Scope, [Mapping(Root)]);
		Assert.Equal(ExternalCategoryMappingDisposition.Unassigned, actual.Disposition);
	}

	[Fact]
	public void OtherProviderAndBudgetMappingsDoNotResolve()
	{
		ExternalCategoryMappingResolution actual = ExternalCategoryMappingPolicy.ResolveRecorded(Assignment, Scope,
			[Mapping(Leaf) with { Scope = new("other", "budget-a") }, Mapping(Parent) with { Scope = new("ynab", "budget-b") }]);
		Assert.Equal(ExternalCategoryMappingDisposition.Unmapped, actual.Disposition);
	}

	[Fact]
	public void BrokenRecordedPathCannotSilentlyChooseDirectMapping()
	{
		ExternalCategoryMappingResolution actual = ExternalCategoryMappingPolicy.ResolveRecorded(
			Assignment with { RecordedPath = [Path[0], Path[2]] }, Scope, [Mapping(Leaf)]);
		Assert.Equal(ExternalCategoryMappingDisposition.InvalidAncestry, actual.Disposition);
	}

	[Fact]
	public void CurrentCycleAndMissingParentFailClosed()
	{
		foreach (TaxonomyNode[] nodes in new[] { new[] { Path[2] }, new[] { Path[2] with { ParentId = Leaf } } })
		{
			ExternalCategoryMappingResolution actual = ExternalCategoryMappingPolicy.ResolveCurrent(Leaf, Revision, nodes, Scope, [Mapping(Leaf)]);
			Assert.Equal(ExternalCategoryMappingDisposition.InvalidAncestry, actual.Disposition);
		}
	}

	[Fact]
	public void ArchivedCurrentCategoryCannotResolveButHistoricalEvidenceIsRetained()
	{
		TaxonomyNode[] archived = [Path[0], Path[1], Path[2] with { Archived = true }];
		Assert.Equal(ExternalCategoryMappingDisposition.InvalidAncestry,
			ExternalCategoryMappingPolicy.ResolveCurrent(Leaf, Revision, archived, Scope, [Mapping(Leaf)]).Disposition);
		Assert.Equal(ExternalCategoryMappingDisposition.Mapped,
			ExternalCategoryMappingPolicy.ResolveRecorded(Assignment with { RecordedPath = archived }, Scope, [Mapping(Leaf)]).Disposition);
	}

	[Fact]
	public void LegacyNameAmbiguityIsPreservedWithAllStableCandidates()
	{
		LegacyExternalCategoryBinding actual = ExternalCategoryMappingPolicy.BindLegacyName("Leaf",
			[Path[2], new(Parent, Root, "Leaf", null, false)]);
		Assert.Equal(LegacyExternalCategoryDisposition.Ambiguous, actual.Disposition);
		Assert.Equal("Leaf", actual.LegacyName);
		Assert.Equal(new[] { Leaf, Parent }.Order(), actual.CandidateCategoryIds.Order());
		Assert.Null(actual.CategoryId);
	}

	[Theory]
	[InlineData("Leaf", LegacyExternalCategoryDisposition.Unique)]
	[InlineData("leaf", LegacyExternalCategoryDisposition.Unmatched)]
	[InlineData(" Leaf ", LegacyExternalCategoryDisposition.Unmatched)]
	public void LegacyBindingUsesOnlyExactNames(string name, LegacyExternalCategoryDisposition expected)
	{
		LegacyExternalCategoryBinding actual = ExternalCategoryMappingPolicy.BindLegacyName(name, Path);
		Assert.Equal(expected, actual.Disposition);
		Assert.Equal(expected == LegacyExternalCategoryDisposition.Unique ? Leaf : (Guid?)null, actual.CategoryId);
	}

	[Fact]
	public void NoAssignmentRemainsUnassignedInBothModes()
	{
		Assert.Equal(ExternalCategoryMappingDisposition.Unassigned,
				ExternalCategoryMappingPolicy.ResolveRecorded(null, Scope, [Mapping(Root)]).Disposition);
		Assert.Equal(ExternalCategoryMappingDisposition.Unassigned,
				ExternalCategoryMappingPolicy.ResolveCurrent(null, Revision, Path, Scope, [Mapping(Root)]).Disposition);
	}

	[Fact]
	public void MalformedRecordedPathsFailBeforeMappingLookup()
	{
		TaxonomyNode[][] invalidPaths = [[], [Path[1], Path[2]], [Path[0], Path[1]],
						[Path[0], Path[1], Path[1], Path[2]],
						[Path[0], Path[1], Path[2] with { ParentId = Root }]];
		foreach (TaxonomyNode[] path in invalidPaths)
		{
			Assert.Equal(ExternalCategoryMappingDisposition.InvalidAncestry,
					ExternalCategoryMappingPolicy.ResolveRecorded(Assignment with { RecordedPath = path },
							Scope, [Mapping(Leaf)]).Disposition);
		}
	}

	[Fact]
	public void DuplicateCurrentIdentityAndArchivedAncestorFailClosed()
	{
		TaxonomyNode[][] invalidTrees = [[.. Path, Path[1]],
						[Path[0], Path[1] with { Archived = true }, Path[2]]];
		foreach (TaxonomyNode[] nodes in invalidTrees)
		{
			Assert.Equal(ExternalCategoryMappingDisposition.InvalidAncestry,
					ExternalCategoryMappingPolicy.ResolveCurrent(Leaf, Revision, nodes, Scope,
							[Mapping(Leaf)]).Disposition);
		}
	}

	[Fact]
	public void AmbiguousNearestAncestorDoesNotFallBackButDirectMappingStillWins()
	{
		ExternalCategoryMapping[] mappings = [Mapping(Root), Mapping(Parent), Mapping(Parent)];
		Assert.Equal(ExternalCategoryMappingDisposition.AmbiguousMapping,
				ExternalCategoryMappingPolicy.ResolveRecorded(Assignment, Scope, mappings).Disposition);
		ExternalCategoryMapping direct = Mapping(Leaf);
		Assert.Equal(direct, ExternalCategoryMappingPolicy.ResolveRecorded(Assignment, Scope,
				[.. mappings, direct]).Mapping);
	}

	[Fact]
	public void ScopeIdentifiersAreCaseSensitiveAndNamesNeverSubstituteForIdentity()
	{
		ExternalCategoryMapping[] mappings = [Mapping(Leaf) with { Scope = new("YNAB", "budget-a") },
						Mapping(Leaf) with { Scope = new("ynab", "BUDGET-A") }, Mapping(Guid.NewGuid())];
		Assert.Equal(ExternalCategoryMappingDisposition.Unmapped,
				ExternalCategoryMappingPolicy.ResolveRecorded(Assignment, Scope, mappings).Disposition);
	}

	[Fact]
	public void LegacyMigrationRetainsArchivedCandidatesAndDeduplicatesStableIdentity()
	{
		LegacyExternalCategoryBinding unique = ExternalCategoryMappingPolicy.BindLegacyName("Leaf",
				[Path[2], Path[2] with { Archived = true }]);
		Assert.Equal(LegacyExternalCategoryDisposition.Unique, unique.Disposition);
		Assert.Equal(Leaf, Assert.Single(unique.CandidateCategoryIds));
		LegacyExternalCategoryBinding ambiguous = ExternalCategoryMappingPolicy.BindLegacyName("Leaf",
				[Path[2], new(Guid.NewGuid(), null, "Leaf", null, true)]);
		Assert.Equal(LegacyExternalCategoryDisposition.Ambiguous, ambiguous.Disposition);
	}
}
