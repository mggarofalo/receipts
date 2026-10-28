using Application.Normalization;
using Application.Taxonomy;
using Domain.Taxonomy;
using Infrastructure.Entities.Taxonomy;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	// Unwired consistency checks only. Success does not establish historical mapping-head
	// eligibility, manifest request authority, phase authority or permission to apply an effect.
	internal static void ValidateEffectMappingClaimCandidate(State history,
		OrganizationEffectManifestEntity manifest, OrganizationEffectItemEntity item,
		OrganizationEffectContractV1.ItemRequest request)
	{
		Validate(history); // Reconstruct ordinary mapping commands/hashes and taxonomy snapshots first.
		_ = OrganizationEffectContractV1.WriteRequest(request);
		Require(request.ManifestId == manifest.Id && item.ManifestId == manifest.Id
			&& request.Actor == manifest.Actor && request.PolicyVersion == manifest.PolicyVersion
			&& request.CapturedAt == manifest.CapturedAt,
			"Effect request differs from frozen manifest.");
		var source = request.Source;
		Require(source.ReceiptId == item.ReceiptId && source.ItemId == item.ItemId
			&& source.Store == item.Store && source.RawDescription == item.RawDescription
			&& source.ItemCode == item.ItemCode && source.ReceiptRevision == item.ReceiptRevision
			&& source.ItemRevision == item.ItemRevision
			&& request.ExpectedAssignmentId == item.ExpectedAssignmentId
			&& request.ApplyOperationId == item.ApplyOperationId && request.UndoOperationId == item.UndoOperationId
			&& request.Target.CategoryId == item.TargetCategoryId
			&& request.Target.TaxonomyRevisionId == item.TaxonomyRevisionId
			&& item.TaxonomyRevisionId == manifest.TaxonomyRevisionId,
			"Effect request differs from frozen item.");
		Require(request.Decision == OrganizationEffectContractV1.ReadDecision(item.DecisionEvidenceJson),
			"Effect mapping claim differs from frozen decision.");

		var decision = request.Decision;
		var mapping = history.MappingRevisions.SingleOrDefault(r => r.Id == decision.MappingRevisionId);
		Require(mapping != null && mapping.CategoryId == item.TargetCategoryId
			&& mapping.TaxonomyRevisionId == decision.ApprovalTaxonomyRevisionId
			&& mapping.RecordedAt <= manifest.CapturedAt,
			"Effect mapping reference lacks matching approval evidence.");
		NormalizationSourceKey normalized;
		try { normalized = NormalizationCatalogPolicy.Normalize(new(item.Store, item.RawDescription, item.ItemCode)); }
		catch (NormalizationCatalogException exception)
		{ throw new InvalidDataException("Invalid effect mapping source.", exception); }
		Require(mapping!.Location == normalized.Location && mapping.Description == normalized.Description
			&& mapping.ItemCode == normalized.ItemCode
			&& mapping.SourceFingerprint == decision.SourceFingerprint
			&& decision.SourceFingerprint == NormalizationCatalogPolicy.Hash(normalized),
			"Effect mapping source tuple or fingerprint differs.");

		Require(history.Revisions.Any(r => r.Id == item.TaxonomyRevisionId && r.RecordedAt <= manifest.CapturedAt),
			"Effect target taxonomy was unavailable at capture.");
		var nodes = history.Nodes.Where(n => n.RevisionId == item.TaxonomyRevisionId)
			.Select(n => new TaxonomyNode(n.CategoryId, n.ParentId, n.Name, n.Definition, n.Archived)).ToList();
		string? semantics = CategoryMappingPolicy.SemanticFingerprint(nodes, item.TargetCategoryId);
		Require(semantics != null && semantics == mapping.SemanticFingerprint,
			"Effect target semantics differ from approved mapping.");
		var byId = nodes.ToDictionary(n => n.Id);
		List<Guid> path = [];
		for (Guid? cursor = item.TargetCategoryId; cursor is Guid id; cursor = byId[id].ParentId) { path.Add(id); }
		Require(request.Target.Path.SequenceEqual(path), "Effect target path differs from immutable taxonomy.");
	}
}
