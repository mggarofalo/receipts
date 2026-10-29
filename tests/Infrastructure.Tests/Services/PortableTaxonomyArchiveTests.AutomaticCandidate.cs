using System.Security.Cryptography;
using System.Text.Json;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	private static (PortableTaxonomyArchive.State State, HistoricalOperationEntity Header, HistoricalOperationItemEntity Item) AutomaticState()
	{
		var state = MappingState(); var mapping = state.MappingRevisions[^1];
		var group = new HistoricalGroupKey(mapping.Location, mapping.Description, mapping.ItemCode);
		Guid[] excluded = [];
		var header = new HistoricalOperationEntity
		{
			Id = Guid.NewGuid(),
			Kind = HistoricalOperationKind.ApprovedMappingV1,
			ApprovedMappingRevisionId = mapping.Id,
			Actor = "actor",
			TaxonomyRevisionId = mapping.TaxonomyRevisionId,
			CapturedAt = mapping.RecordedAt.AddSeconds(1),
			Reason = "Approved exact-source mapping capture.",
			SelectionJson = JsonSerializer.Serialize(new[] { new { Group = group, TargetCategoryId = mapping.CategoryId, ExcludedItemIds = excluded } })
		};
		header.RequestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
		{ header.Actor, header.TaxonomyRevisionId, MappingRevisionId = mapping.Id, Group = group, ExcludedItemIds = excluded })));
		var item = new HistoricalOperationItemEntity
		{
			OperationId = header.Id,
			ApprovedMappingRevisionId = mapping.Id,
			ItemId = Guid.NewGuid(),
			ReceiptId = Guid.NewGuid(),
			Store = group.Store,
			RawDescription = group.RawDescription,
			ItemCode = group.ItemCode,
			ItemRevision = Guid.NewGuid(),
			ReceiptRevision = Guid.NewGuid(),
			TargetCategoryId = mapping.CategoryId,
			ApplyOperationId = Guid.NewGuid(),
			UndoOperationId = Guid.NewGuid()
		};
		return (state, header, item);
	}

	[Fact]
	public void AutomaticCandidate_ValidatesZeroItemCaptureAndBindsHeaderMapping()
	{
		var (state, header, item) = AutomaticState();
		FluentActions.Invoking(() => PortableAutomaticHistoricalV14.Validate(state, header, [])).Should().NotThrow();
		FluentActions.Invoking(() => PortableAutomaticHistoricalV14.Validate(state, header, [item])).Should().NotThrow();
		header.ApprovedMappingRevisionId = state.MappingRevisions[0].Id;
		FluentActions.Invoking(() => PortableAutomaticHistoricalV14.Validate(state, header, [])).Should().Throw<InvalidDataException>();
	}

	[Theory]
	[InlineData("mapping")]
	[InlineData("item")]
	[InlineData("hash")]
	[InlineData("outcome")]
	public void AutomaticCandidate_RejectsContradictoryEvidence(string corruption)
	{
		var (state, header, item) = AutomaticState();
		switch (corruption)
		{
			case "mapping": item.ApprovedMappingRevisionId = Guid.NewGuid(); break;
			case "item": item.RawDescription = "Other"; break;
			case "hash": header.RequestHash = new string('A', 64); break;
			case "outcome": item.State = HistoricalItemState.Applied; break;
		}
		FluentActions.Invoking(() => PortableAutomaticHistoricalV14.Validate(state, header, [item])).Should().Throw<InvalidDataException>();
	}

	[Fact]
	public void AutomaticCandidate_AppliedOutcomeMustMatchAssignmentAndOperationHash()
	{
		var (state, header, item) = AutomaticState();
		var mapping = state.MappingRevisions[^1];
		var evidence = new CategoryMappingReuseEvidence(mapping.Id, mapping.TaxonomyRevisionId, mapping.SourceFingerprint);
		var command = new ClassificationCommand(item.TargetCategoryId, header.TaxonomyRevisionId, null, item.ApplyOperationId, JsonSerializer.Serialize(evidence));
		Guid itemId = item.ItemId;
		string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { itemId, command, mappingEvidence = evidence })));
		var assignment = new PurchaseClassificationEntity
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = itemId,
			Actor = header.Actor,
			OperationId = item.ApplyOperationId,
			CategoryId = item.TargetCategoryId,
			TaxonomyRevisionId = header.TaxonomyRevisionId,
			Reason = command.Reason,
			Source = "approved_mapping",
			RecordedAt = header.CapturedAt.AddSeconds(1)
		};
		state.Assignments.Add(assignment);
		state.ClassificationOperations.Add(new() { Actor = header.Actor, OperationId = item.ApplyOperationId, AssignmentId = assignment.Id, RequestHash = hash });
		header.State = HistoricalOperationState.Completed; item.State = HistoricalItemState.Applied; item.AppliedAssignmentId = assignment.Id;
		FluentActions.Invoking(() => PortableAutomaticHistoricalV14.Validate(state, header, [item])).Should().NotThrow();
		assignment.Reason = "{}";
		FluentActions.Invoking(() => PortableAutomaticHistoricalV14.Validate(state, header, [item])).Should().Throw<InvalidDataException>();
	}

	[Theory]
	[InlineData("manual", null, false, false)]
	[InlineData("manual", null, true, false)]
	[InlineData("approved_mapping", null, false, false)]
	[InlineData("legacy_write", "resolved_path", false, false)]
	[InlineData("legacy_import", null, true, false)]
	[InlineData("legacy_import", null, false, true)]
	[InlineData("legacy_write", "unresolved_path", false, true)]
	public void AutomaticCandidate_RequiresEligiblePreviousAssignment(string source, string? resolution, bool categorized, bool eligible)
	{
		var (state, header, item) = AutomaticState();
		var prior = new PurchaseClassificationEntity
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = item.ItemId,
			Source = source,
			LegacyResolution = resolution,
			CategoryId = categorized ? item.TargetCategoryId : null,
			RecordedAt = header.CapturedAt.AddSeconds(-1)
		};
		state.Assignments.Add(prior); item.ExpectedAssignmentId = prior.Id; item.PreviousCategoryId = prior.CategoryId;
		Action validate = () => PortableAutomaticHistoricalV14.Validate(state, header, [item]);
		if (eligible) { validate.Should().NotThrow(); }
		else { validate.Should().Throw<InvalidDataException>().WithMessage("*ineligible*"); }
	}

	[Theory]
	[InlineData("none")]
	[InlineData("assignment")]
	[InlineData("hash")]
	[InlineData("source")]
	[InlineData("reason")]
	[InlineData("predecessor")]
	public void AutomaticCandidate_ValidatesAppliedAndUndoneLineage(string mutation)
	{
		var (state, header, item) = AutomaticState(); var mapping = state.MappingRevisions[^1];
		var evidence = new CategoryMappingReuseEvidence(mapping.Id, mapping.TaxonomyRevisionId, mapping.SourceFingerprint);
		Guid itemId = item.ItemId;
		var apply = new ClassificationCommand(item.TargetCategoryId, header.TaxonomyRevisionId, null, item.ApplyOperationId, JsonSerializer.Serialize(evidence));
		var applied = new PurchaseClassificationEntity
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = itemId,
			Actor = header.Actor,
			OperationId = apply.OperationId,
			CategoryId = apply.CategoryId,
			TaxonomyRevisionId = apply.TaxonomyRevisionId,
			Reason = apply.Reason,
			Source = "approved_mapping",
			RecordedAt = header.CapturedAt.AddSeconds(1)
		};
		state.Assignments.Add(applied);
		state.ClassificationOperations.Add(new()
		{
			Actor = header.Actor,
			OperationId = apply.OperationId,
			AssignmentId = applied.Id,
			RequestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { itemId, command = apply, mappingEvidence = evidence })))
		});
		item.State = HistoricalItemState.Applied; item.AppliedAssignmentId = applied.Id;
		header.State = HistoricalOperationState.Undone; header.UndoTaxonomyRevisionId = header.TaxonomyRevisionId; header.UndoReason = "undo";
		var undo = HistoricalReclassificationPolicy.UndoCommand(item.Evidence(), applied.Id, header.UndoTaxonomyRevisionId.Value, header.UndoReason);
		var undone = new PurchaseClassificationEntity
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = itemId,
			Actor = header.Actor,
			OperationId = undo.OperationId,
			PreviousAssignmentId = applied.Id,
			CategoryId = undo.CategoryId,
			TaxonomyRevisionId = undo.TaxonomyRevisionId,
			Reason = undo.Reason,
			Source = "manual",
			RecordedAt = header.CapturedAt.AddSeconds(2)
		};
		state.Assignments.Add(undone);
		var undoOperation = new ClassificationOperationEntity
		{
			Actor = header.Actor,
			OperationId = undo.OperationId,
			AssignmentId = undone.Id,
			RequestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { itemId, command = undo })))
		};
		state.ClassificationOperations.Add(undoOperation); item.UndoState = HistoricalItemState.Applied; item.UndoAssignmentId = undone.Id;
		switch (mutation)
		{
			case "assignment": item.UndoAssignmentId = Guid.NewGuid(); break;
			case "hash": undoOperation.RequestHash = new string('A', 64); break;
			case "source": undone.Source = "approved_mapping"; break;
			case "reason": undone.Reason = "changed"; break;
			case "predecessor": undone.PreviousAssignmentId = Guid.NewGuid(); break;
		}
		Action validate = () => PortableAutomaticHistoricalV14.Validate(state, header, [item]);
		if (mutation == "none") { validate.Should().NotThrow(); }
		else { validate.Should().Throw<InvalidDataException>(); }
	}

	[Fact]
	public void AutomaticCandidate_WireRequiresProvenanceWhilePublicExportRemainsV13()
	{
		var (_, header, item) = AutomaticState();
		var wire = new PortableAutomaticHistoricalV14.HeaderWire(header, header.Kind, header.ApprovedMappingRevisionId);
		string json = JsonSerializer.Serialize(wire);
		PortableAutomaticHistoricalV14.ReadHeader(json).Header.Id.Should().Be(header.Id);
		var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
		node["Header"]!.AsObject().Remove("ApprovedMappingRevisionId");
		FluentActions.Invoking(() => PortableAutomaticHistoricalV14.ReadHeader(node.ToJsonString())).Should().Throw<InvalidDataException>();
		var itemWire = new PortableAutomaticHistoricalV14.ItemWire(item, item.ApprovedMappingRevisionId);
		PortableAutomaticHistoricalV14.ReadItem(JsonSerializer.Serialize(itemWire)).Item.ItemId.Should().Be(item.ItemId);
		PortableBackupFormat.CurrentVersion.Should().Be(14);
		PortableBackupFormat.LatestImportVersion.Should().Be(14);
		PortableBackupFormat.RequiredTablesFor(14).Should().Contain([
			"historical_operation_archive", "source_revision_archive", "review_disposition_archive", "review_preference_archive"]);
	}
}
