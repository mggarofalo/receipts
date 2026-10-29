using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Application.Classification;
using Application.Normalization;
using Application.Taxonomy;
using Domain.Taxonomy;
using Infrastructure.Entities.Taxonomy;

namespace Infrastructure.Services;

// v14 automatic history codec.
internal static class PortableAutomaticHistoricalV14
{
	internal const int Version = 14;
	internal sealed record Scope(HistoricalGroupKey Group, Guid? TargetCategoryId, Guid[] ExcludedItemIds);
	// The wrapper makes all provenance fields mandatory, including nullable values, in this format.
	internal sealed record HeaderWire(HistoricalOperationEntity Header, HistoricalOperationKind Kind, Guid? ApprovedMappingRevisionId);
	internal sealed record ItemWire(HistoricalOperationItemEntity Item, Guid? ApprovedMappingRevisionId);

	internal static HeaderWire ReadHeader(string json)
	{
		var wire = PortableTaxonomyArchive.DeserializeStrict<HeaderWire>(json) ?? throw new InvalidDataException("Null automatic header.");
		Require(wire.Header != null && wire.Kind == wire.Header.Kind && wire.ApprovedMappingRevisionId == wire.Header.ApprovedMappingRevisionId, "Header provenance differs from wire.");
		RequireFields(json, "Header", "Kind", "ApprovedMappingRevisionId");
		return wire;
	}

	internal static ItemWire ReadItem(string json)
	{
		var wire = PortableTaxonomyArchive.DeserializeStrict<ItemWire>(json) ?? throw new InvalidDataException("Null automatic item.");
		Require(wire.Item != null && wire.ApprovedMappingRevisionId == wire.Item.ApprovedMappingRevisionId, "Item provenance differs from wire.");
		RequireFields(json, "Item", "ApprovedMappingRevisionId");
		return wire;
	}

	private static void RequireFields(string json, string nested, params string[] names)
	{
		using var document = JsonDocument.Parse(json);
		var value = document.RootElement.GetProperty(nested);
		foreach (string name in names) { Require(value.TryGetProperty(name, out _), "Missing candidate provenance field: " + name); }
	}

	internal static void Validate(PortableTaxonomyArchive.State state, HistoricalOperationEntity header, IReadOnlyList<HistoricalOperationItemEntity> items)
	{
		Require(header.Kind == HistoricalOperationKind.ApprovedMappingV1 && header.Id != Guid.Empty
			&& header.ApprovedMappingRevisionId is Guid id && id != Guid.Empty && Text(header.Actor)
			&& Text(header.Reason) && header.TaxonomyRevisionId != Guid.Empty && header.CapturedAt != default && header.CapturedAt.Offset == TimeSpan.Zero
			&& state.Revisions.Any(r => r.Id == header.TaxonomyRevisionId && r.RecordedAt <= header.CapturedAt),
			"Invalid automatic header.");
		var scopes = PortableTaxonomyArchive.DeserializeStrict<Scope[]>(header.SelectionJson);
		Require(scopes is { Length: 1 } && scopes[0] != null && scopes[0].Group != null && scopes[0].ExcludedItemIds != null
			&& scopes[0].TargetCategoryId is Guid target && target != Guid.Empty, "Invalid automatic scope.");
		var scope = scopes![0]; var group = scope.Group; var excluded = scope.ExcludedItemIds;
		Require(Text(group.Store) && Text(group.RawDescription) && group.ItemCode?.Contains('\0') != true
			&& excluded.Length <= 1000 && excluded.All(i => i != Guid.Empty) && excluded.Distinct().Count() == excluded.Length
			&& excluded.SequenceEqual(excluded.Order()), "Invalid automatic source or exclusions.");
		NormalizationSourceKey source = new(group.Store, group.RawDescription, group.ItemCode);
		Require(source == NormalizationCatalogPolicy.Normalize(source), "Automatic source is not canonical.");
		var mapping = state.MappingRevisions.SingleOrDefault(m => m.Id == header.ApprovedMappingRevisionId);
		Require(mapping != null && mapping.CategoryId == scope.TargetCategoryId
			&& mapping.Location == group.Store && mapping.Description == group.RawDescription && mapping.ItemCode == group.ItemCode
			&& mapping.SourceFingerprint == NormalizationCatalogPolicy.Hash(source), "Automatic mapping differs from capture.");
		string? semantics = CategoryMappingPolicy.SemanticFingerprint(state.Nodes.Where(n => n.RevisionId == header.TaxonomyRevisionId)
			.Select(n => new TaxonomyNode(n.CategoryId, n.ParentId, n.Name, n.Definition, n.Archived)).ToList(), scope.TargetCategoryId!.Value);
		Require(semantics != null && semantics == mapping!.SemanticFingerprint, "Automatic mapping semantics differ from capture.");
		string digest = Hash(new { header.Actor, header.TaxonomyRevisionId, MappingRevisionId = header.ApprovedMappingRevisionId!.Value, Group = group, ExcludedItemIds = excluded });
		Require(header.RequestHash == digest, "Automatic request hash differs from intent.");
		Require(items.Count <= 100 && items.Select(i => i.ItemId).Distinct().Count() == items.Count, "Invalid automatic manifest size or duplicate.");
		// Integration must enforce uniqueness across every manual and automatic manifest in the
		// retained destination union. This local set covers this one header only.
		HashSet<Guid> operations = [];
		foreach (var row in items)
		{
			Require(row.OperationId == header.Id && row.ApprovedMappingRevisionId == mapping.Id && row.ItemId != Guid.Empty
				&& row.ReceiptId != Guid.Empty && row.ItemRevision != Guid.Empty && row.ReceiptRevision != Guid.Empty
				&& !excluded.Contains(row.ItemId) && row.TargetCategoryId == scope.TargetCategoryId
				&& new HistoricalGroupKey(row.Store, row.RawDescription, row.ItemCode) == group
				&& row.ApplyOperationId != Guid.Empty && row.UndoOperationId != Guid.Empty
				&& operations.Add(row.ApplyOperationId) && operations.Add(row.UndoOperationId), "Invalid automatic manifest item.");
			var prior = row.ExpectedAssignmentId is Guid priorId ? state.Assignments.SingleOrDefault(a => a.Id == priorId) : null;
			Require(row.ExpectedAssignmentId is null ? row.PreviousCategoryId == null
				: prior != null && prior.ReceiptItemId == row.ItemId && prior.CategoryId == row.PreviousCategoryId && prior.RecordedAt <= header.CapturedAt,
				"Invalid previous assignment.");
			PurchaseClassification? previous = prior == null ? null : new(prior.Id, prior.ReceiptItemId, prior.Sequence,
				prior.PreviousAssignmentId, prior.CategoryId, prior.TaxonomyRevisionId, prior.RecordedAt, prior.Actor,
				prior.Source, prior.Reason, prior.OperationId, [], prior.LegacyCategory, prior.LegacySubcategory, prior.LegacyResolution);
			// Availability and suppression are not archived capture evidence; these flags only let the
			// shared policy evaluate the immutable assignment provenance present in the archive.
			Require(OrganizationEffectPolicy.Eligibility(previous, available: true, leaveAsIs: false, suppressed: false)
				== OrganizationEligibility.Eligible, "Previous assignment was ineligible for automatic capture.");
			ValidateOutcome(state, header, row, mapping, false);
			ValidateOutcome(state, header, row, mapping, true);
		}
		Require(Enum.IsDefined(header.State) && (header.State != HistoricalOperationState.Preview || items.All(i => i.State == HistoricalItemState.Pending))
			&& (header.State != HistoricalOperationState.Completed || items.All(i => i.State != HistoricalItemState.Pending))
			&& (header.State != HistoricalOperationState.Undone || items.All(i => i.State != HistoricalItemState.Applied || i.UndoState != HistoricalItemState.Pending)), "Invalid automatic progress.");
		bool undo = header.UndoTaxonomyRevisionId != null;
		Require(undo == (header.State is HistoricalOperationState.Undoing or HistoricalOperationState.Undone)
			&& (undo ? Text(header.UndoReason) && state.Revisions.Any(r => r.Id == header.UndoTaxonomyRevisionId) : header.UndoReason == null), "Invalid undo intent.");
	}

	private static void ValidateOutcome(PortableTaxonomyArchive.State state, HistoricalOperationEntity header, HistoricalOperationItemEntity row, CategoryMappingRevisionEntity mapping, bool undo)
	{
		var status = undo ? row.UndoState : row.State;
		var assignmentId = undo ? row.UndoAssignmentId : row.AppliedAssignmentId;
		var reason = undo ? row.UndoReason : row.Reason;
		var operationId = undo ? row.UndoOperationId : row.ApplyOperationId;
		Require(Enum.IsDefined(status) && (!undo || status == HistoricalItemState.Pending || (row.State == HistoricalItemState.Applied && header.UndoTaxonomyRevisionId != null)), "Invalid automatic outcome.");
		if (status != HistoricalItemState.Applied)
		{
			Require(assignmentId == null && (status == HistoricalItemState.Pending ? reason == null : Text(reason))
				&& !state.ClassificationOperations.Any(o => o.Actor == header.Actor && o.OperationId == operationId), "Invalid automatic non-applied outcome.");
			return;
		}
		Require(assignmentId != null && (!undo || (row.AppliedAssignmentId != null && header.UndoTaxonomyRevisionId != null)), "Automatic outcome lacks assignment.");
		var evidence = new CategoryMappingReuseEvidence(mapping.Id, mapping.TaxonomyRevisionId, mapping.SourceFingerprint);
		var command = undo ? HistoricalReclassificationPolicy.UndoCommand(row.Evidence(), row.AppliedAssignmentId!.Value, header.UndoTaxonomyRevisionId!.Value, header.UndoReason!)
			: new ClassificationCommand(row.TargetCategoryId, header.TaxonomyRevisionId, row.ExpectedAssignmentId, operationId, JsonSerializer.Serialize(evidence));
		Guid itemId = row.ItemId;
		string hash = undo ? Hash(new { itemId, command }) : Hash(new { itemId, command, mappingEvidence = evidence });
		Require(reason == null && state.Assignments.Any(a => a.Id == assignmentId && a.ReceiptItemId == itemId && a.Actor == header.Actor
			&& a.OperationId == operationId && a.PreviousAssignmentId == command.ExpectedAssignmentId && a.CategoryId == command.CategoryId
			&& a.TaxonomyRevisionId == command.TaxonomyRevisionId && a.Reason == command.Reason && a.Source == (undo ? "manual" : "approved_mapping")
			&& a.RecordedAt >= header.CapturedAt && state.ClassificationOperations.Any(o => o.Actor == header.Actor && o.OperationId == operationId
				&& o.AssignmentId == a.Id && o.RequestHash == hash)), "Automatic outcome differs from classification history.");
	}

	private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0');
	private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
	private static void Require([DoesNotReturnIf(false)] bool condition, string message) { if (!condition) { throw new InvalidDataException(message); } }
}
