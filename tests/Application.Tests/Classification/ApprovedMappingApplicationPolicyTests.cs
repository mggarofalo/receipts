using Application.Classification;
using Application.Normalization;
using Application.Taxonomy;
using Domain.Taxonomy;
using FluentAssertions;

namespace Application.Tests.Classification;

public sealed class ApprovedMappingApplicationPolicyTests
{
	private static readonly Guid Item = Guid.NewGuid();
	private static readonly Guid Taxonomy = Guid.NewGuid();
	private static readonly Guid Category = Guid.NewGuid();
	private static readonly TaxonomyNode Node = new(Category, null, "Food", "definition", false);
	private static readonly HistoricalGroupKey Source = new("Store", " Milk ", null);
	private static readonly NormalizationSourceKey Key = new("Store", "Milk", null);
	private static readonly CategoryMappingRevision Mapping = new(Guid.NewGuid(), null, Key,
			NormalizationCatalogPolicy.Hash(Key), Category, Guid.NewGuid(),
			CategoryMappingPolicy.SemanticFingerprint([Node], Category), "reviewer", Guid.NewGuid(), "reason", DateTimeOffset.UtcNow);
	private static readonly ApprovedMappingCandidate Candidate = new(Item, Source, Guid.NewGuid(), Guid.NewGuid(), null, Mapping.Id, Taxonomy);
	private static readonly ApprovedMappingCurrent Current = new(Item, Source, Candidate.ItemRevision, Candidate.ReceiptRevision,
			null, null, true, false, false, null, null, Taxonomy, Mapping.Id, Mapping, [Node]);

	[Fact]
	public void ExactCurrentApprovalForAbsentPurchaseIsEligible() =>
			ApprovedMappingApplicationPolicy.CanApply(Candidate, Current).Should().BeTrue();

	[Theory]
	[InlineData("legacy_import", null)]
	[InlineData("legacy_write", "unresolved_path")]
	public void KnownUnresolvedObservationIsEligible(string source, string? resolution)
	{
		PurchaseClassification assignment = Assignment(source, null, resolution);
		ApprovedMappingApplicationPolicy.CanApply(Candidate with { AssignmentId = assignment.Id },
				Current with { Assignment = assignment, AssignmentId = assignment.Id }).Should().BeTrue();
	}

	[Theory]
	[InlineData("manual", null)]
	[InlineData("approved_mapping", null)]
	[InlineData("legacy_write", null)]
	[InlineData("unknown", "unresolved_path")]
	public void ManualClearAndUnknownProvenanceAreProtected(string source, string? resolution)
	{
		PurchaseClassification assignment = Assignment(source, null, resolution);
		ApprovedMappingApplicationPolicy.CanApply(Candidate with { AssignmentId = assignment.Id },
				Current with { Assignment = assignment, AssignmentId = assignment.Id }).Should().BeFalse();
	}

	[Fact]
	public void ExistingCategoryIsProtectedEvenOnLegacyObservation()
	{
		PurchaseClassification assignment = Assignment("legacy_import", Category, null);
		ApprovedMappingApplicationPolicy.CanApply(Candidate with { AssignmentId = assignment.Id },
				Current with { Assignment = assignment, AssignmentId = assignment.Id }).Should().BeFalse();
	}

	[Fact]
	public void MissingSuppressedAndStaleEvidenceFailsClosed()
	{
		ApprovedMappingCurrent[] changes = [
				Current with { Available = null }, Current with { Available = false },
						Current with { LeaveAsIs = null }, Current with { LeaveAsIs = true },
						Current with { Suppressed = null }, Current with { Suppressed = true },
						Current with { LegacyCategory = "Food" }, Current with { LegacySubcategory = "Other" },
						Current with { AssignmentId = Guid.NewGuid() }, Current with { ItemId = Guid.NewGuid() },
						Current with { ItemRevision = Guid.NewGuid() }, Current with { ReceiptRevision = Guid.NewGuid() },
						Current with { Source = Source with { RawDescription = "Milk" } },
						Current with { MappingHeadId = Guid.NewGuid() }, Current with { MappingHeadId = null },
						Current with { Mapping = null }, Current with { Mapping = Mapping with { CategoryId = null } },
						Current with { Mapping = Mapping with { Source = Key with { Location = "Other" } } },
						Current with { Mapping = Mapping with { SourceFingerprint = "wrong" } },
						Current with { Mapping = Mapping with { SemanticFingerprint = "wrong" } },
						Current with { TaxonomyRevisionId = Guid.NewGuid() }, Current with { TaxonomyNodes = null },
						Current with { TaxonomyNodes = [Node with { Archived = true }] },
						Current with { TaxonomyNodes = [Node, Node] }
		];
		foreach (ApprovedMappingCurrent changed in changes)
		{ ApprovedMappingApplicationPolicy.CanApply(Candidate, changed).Should().BeFalse(); }
	}

	[Fact]
	public void AssignmentEvidenceMustMatchThePurchaseAndCapturedPointer()
	{
		PurchaseClassification assignment = Assignment("legacy_import", null, null);
		ApprovedMappingApplicationPolicy.CanApply(Candidate, Current with { Assignment = assignment }).Should().BeFalse();
		ApprovedMappingApplicationPolicy.CanApply(Candidate with { AssignmentId = assignment.Id },
				Current with { AssignmentId = assignment.Id, Assignment = assignment with { ReceiptItemId = Guid.NewGuid() } }).Should().BeFalse();
	}

	[Fact]
	public void ReplacedAssignmentIsNotTheCapturedObservation()
	{
		PurchaseClassification first = Assignment("legacy_import", null, null);
		PurchaseClassification replacement = Assignment("legacy_import", null, null);
		ApprovedMappingApplicationPolicy.CanApply(Candidate with { AssignmentId = first.Id },
				Current with { Assignment = replacement, AssignmentId = replacement.Id }).Should().BeFalse();
	}

	[Theory]
	[InlineData(null, true)]
	[InlineData("", true)]
	[InlineData(" ", true)]
	[InlineData("abc", false)]
	[InlineData("ABC", false)]
	public void ItemCodeMustMatchTheApprovedNormalizedKey(string? code, bool expected)
	{
		HistoricalGroupKey source = Source with { ItemCode = code };
		ApprovedMappingApplicationPolicy.CanApply(Candidate with { Source = source },
				Current with { Source = source }).Should().Be(expected);
	}

	[Fact]
	public void ItemCodeCaseIsNotFolded()
	{
		NormalizationSourceKey key = Key with { ItemCode = "abc" };
		CategoryMappingRevision mapping = Mapping with { Source = key, SourceFingerprint = NormalizationCatalogPolicy.Hash(key) };
		HistoricalGroupKey source = Source with { ItemCode = "ABC" };
		ApprovedMappingApplicationPolicy.CanApply(Candidate with { Source = source },
				Current with { Source = source, Mapping = mapping }).Should().BeFalse();
	}

	[Fact]
	public void ChangedOrArchivedAncestorInvalidatesApproval()
	{
		TaxonomyNode root = new(Guid.NewGuid(), null, "Root", "original", false);
		TaxonomyNode child = Node with { ParentId = root.Id };
		CategoryMappingRevision mapping = Mapping with { SemanticFingerprint = CategoryMappingPolicy.SemanticFingerprint([root, child], Category) };
		ApprovedMappingCurrent current = Current with { Mapping = mapping, TaxonomyNodes = [root, child] };
		ApprovedMappingApplicationPolicy.CanApply(Candidate, current).Should().BeTrue();
		ApprovedMappingApplicationPolicy.CanApply(Candidate, current with { TaxonomyNodes = [root with { Archived = true }, child] }).Should().BeFalse();
		ApprovedMappingApplicationPolicy.CanApply(Candidate, current with { TaxonomyNodes = [root with { Definition = "changed" }, child] }).Should().BeFalse();
	}
	private static PurchaseClassification Assignment(string source, Guid? category, string? resolution) =>
			new(Guid.NewGuid(), Item, 1, null, category, Taxonomy, DateTimeOffset.UtcNow, "actor", source,
					"reason", Guid.NewGuid(), [], LegacyResolution: resolution);
}
