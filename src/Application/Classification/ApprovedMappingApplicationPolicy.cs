using Application.Normalization;
using Application.Taxonomy;
using Domain.Taxonomy;

namespace Application.Classification;

// Captured evidence identifies a candidate; it is not authorization to write.
public sealed record ApprovedMappingCandidate(Guid ItemId, HistoricalGroupKey Source, Guid ItemRevision,
		Guid ReceiptRevision, Guid? AssignmentId, Guid MappingRevisionId, Guid TaxonomyRevisionId);

// Every fact must be read from authoritative current state in the same transaction as any future effect.
// Null availability/exclusion facts mean unknown, not an affirmative eligibility decision.
public sealed record ApprovedMappingCurrent(Guid ItemId, HistoricalGroupKey Source, Guid ItemRevision,
		Guid ReceiptRevision, PurchaseClassification? Assignment, Guid? AssignmentId,
		bool? Available, bool? LeaveAsIs, bool? Suppressed, string? LegacyCategory, string? LegacySubcategory,
		Guid TaxonomyRevisionId, Guid? MappingHeadId, CategoryMappingRevision? Mapping,
		IReadOnlyList<TaxonomyNode>? TaxonomyNodes);

/// <summary>Inactive pure eligibility and provenance check; it neither authorizes nor performs an effect.</summary>
public static class ApprovedMappingApplicationPolicy
{
	public static bool CanApply(ApprovedMappingCandidate? candidate, ApprovedMappingCurrent? current)
	{
		if (candidate == null || current == null || candidate.Source == null || current.Source == null
				|| candidate.ItemId == Guid.Empty || candidate.ItemId != current.ItemId
				|| candidate.ItemRevision == Guid.Empty || candidate.ReceiptRevision == Guid.Empty
				|| candidate.ItemRevision != current.ItemRevision || candidate.ReceiptRevision != current.ReceiptRevision
				|| candidate.Source != current.Source || candidate.AssignmentId == Guid.Empty
				|| candidate.AssignmentId != current.AssignmentId || current.AssignmentId != current.Assignment?.Id
				|| (current.Assignment != null && current.Assignment.ReceiptItemId != current.ItemId)
				|| current.Available != true || current.LeaveAsIs != false || current.Suppressed != false
				|| !string.IsNullOrWhiteSpace(current.LegacyCategory) || !string.IsNullOrWhiteSpace(current.LegacySubcategory)
				|| OrganizationEffectPolicy.Eligibility(current.Assignment, true, false, false) != OrganizationEligibility.Eligible
				|| candidate.MappingRevisionId == Guid.Empty || current.MappingHeadId != candidate.MappingRevisionId
				|| current.Mapping?.Id != candidate.MappingRevisionId || candidate.TaxonomyRevisionId == Guid.Empty
				|| current.TaxonomyRevisionId != candidate.TaxonomyRevisionId || current.TaxonomyNodes == null)
		{ return false; }

		CategoryMappingRevision mapping = current.Mapping;
		if (mapping.CategoryId is not Guid category || category == Guid.Empty || mapping.TaxonomyRevisionId == Guid.Empty
				|| string.IsNullOrWhiteSpace(mapping.SemanticFingerprint) || mapping.Source == null
				|| mapping.Source.Location == null || mapping.Source.Description == null
				|| current.Source.Store == null || current.Source.RawDescription == null
				|| current.TaxonomyNodes.Any(node => node == null)
				|| current.TaxonomyNodes.Select(node => node.Id).Distinct().Count() != current.TaxonomyNodes.Count)
		{ return false; }
		try
		{
			NormalizationSourceKey source = NormalizationCatalogPolicy.Normalize(
					new(current.Source.Store, current.Source.RawDescription, current.Source.ItemCode));
			return mapping.Source == source && mapping.SourceFingerprint == NormalizationCatalogPolicy.Hash(source)
					&& CategoryMappingPolicy.SemanticFingerprint(current.TaxonomyNodes, category) == mapping.SemanticFingerprint;
		}
		catch (NormalizationCatalogException) { return false; }
	}
}
