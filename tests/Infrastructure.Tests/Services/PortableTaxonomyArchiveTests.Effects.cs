using System.Text.Json;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	private static (PortableTaxonomyArchive.State State, PortableTaxonomyArchive.EffectEvidence Evidence) EffectState()
	{
		var state = EmptyState();
		Guid category = Guid.NewGuid();
		state.Nodes.Add(new() { RevisionId = state.Heads[0].RevisionId, CategoryId = category, Name = "Target" });
		var manifest = new OrganizationEffectManifestEntity
		{
			Id = Guid.NewGuid(),
			Actor = "actor",
			PolicyVersion = "candidate-1",
			RequestHash = new string('A', 64),
			TaxonomyRevisionId = state.Heads[0].RevisionId,
			CapturedAt = state.Revisions[0].RecordedAt
		};
		var item = new OrganizationEffectItemEntity
		{
			ManifestId = manifest.Id,
			ItemId = Guid.NewGuid(),
			ReceiptId = Guid.NewGuid(),
			Store = " Store ",
			RawDescription = "raw",
			ItemRevision = Guid.NewGuid(),
			ReceiptRevision = Guid.NewGuid(),
			TaxonomyRevisionId = manifest.TaxonomyRevisionId,
			TargetCategoryId = category,
			ApplyOperationId = Guid.NewGuid(),
			UndoOperationId = Guid.NewGuid(),
			DecisionEvidenceJson = "{\"z\": [1, null], \"a\": {\"yes\":true}}"
		};
		return (state, new([PortableTaxonomyArchive.SealEffectManifest(manifest, [item])], [item], []));
	}

	[Fact]
	public void Effects_CodecRoundTripsPortableEvidenceWithoutLocalStampsOrReservations()
	{
		var (state, evidence) = EffectState();
		var records = PortableTaxonomyArchive.WriteEffectEvidence(evidence);
		string json = JsonSerializer.Serialize(records);
		json.Should().NotContain("WriteTransaction").And.NotContain("OrganizationEffectOperation");
		var restored = PortableTaxonomyArchive.ReadEffectEvidence(records);
		JsonSerializer.Serialize(restored).Should().Be(JsonSerializer.Serialize(evidence));
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state, restored)).Should().NotThrow();
		PortableBackupFormat.CurrentVersion.Should().Be(13);
	}

	[Fact]
	public void Effects_RejectsMissingOrExpandedSealedMembership()
	{
		var (state, evidence) = EffectState();
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state, evidence with { Items = [] }))
			.Should().Throw<InvalidDataException>().WithMessage("*sealed membership*");
		var row = JsonSerializer.SerializeToNode(evidence.Items[0])!;
		row["ItemId"] = Guid.NewGuid();
		var extra = JsonSerializer.Deserialize<OrganizationEffectItemEntity>(row)!;
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state, evidence with { Items = [.. evidence.Items, extra] }))
			.Should().Throw<InvalidDataException>().WithMessage("*sealed membership*");
	}

	[Theory]
	[InlineData("unknown")]
	[InlineData("wrong-key")]
	[InlineData("duplicate")]
	[InlineData("missing-field")]
	[InlineData("local-stamp")]
	public void Effects_RejectsMalformedPortableRecords(string mutation)
	{
		var (_, evidence) = EffectState();
		var rows = PortableTaxonomyArchive.WriteEffectEvidence(evidence);
		var first = rows[1];
		var node = JsonSerializer.SerializeToNode(evidence.Items[0])!.AsObject();
		switch (mutation)
		{
			case "unknown": rows[1] = first with { Kind = "operation-reservation" }; break;
			case "wrong-key": rows[1] = first with { Id = "{}" }; break;
			case "duplicate": rows.Add(first); break;
			case "missing-field": node.Remove("ExpectedAssignmentId"); rows[1] = first with { Payload = node.ToJsonString() }; break;
			case "local-stamp": node["WriteTransaction"] = 12; rows[1] = first with { Payload = node.ToJsonString() }; break;
		}
		FluentActions.Invoking(() => PortableTaxonomyArchive.ReadEffectEvidence(rows)).Should().Throw<InvalidDataException>();
	}

	[Fact]
	public void Effects_DecisionCanonicalizationIgnoresObjectOrderAndRejectsNestedDuplicateKeys()
	{
		PortableTaxonomyArchive.CanonicalEffectDecision("{\"b\":1,\"a\": {\"y\":false, \"x\":null}}")
			.Should().Be("{\"a\":{\"x\":null,\"y\":false},\"b\":1}");
		FluentActions.Invoking(() => PortableTaxonomyArchive.CanonicalEffectDecision("{\"a\":[{\"x\":1,\"x\":2}]}"))
			.Should().Throw<InvalidDataException>().WithMessage("*Duplicate*");
		FluentActions.Invoking(() => PortableTaxonomyArchive.CanonicalEffectDecision("[]"))
			.Should().Throw<InvalidDataException>();
	}

	[Fact]
	public void Effects_MembershipHashCanonicalizesDecisionObjectsButDetectsIntentChanges()
	{
		var (state, evidence) = EffectState();
		var node = JsonSerializer.SerializeToNode(evidence.Items[0])!;
		node["DecisionEvidenceJson"] = "{\"a\": {\"yes\": true},\"z\":[1,null]}";
		var reordered = JsonSerializer.Deserialize<OrganizationEffectItemEntity>(node)!;
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state, evidence with { Items = [reordered] }))
			.Should().NotThrow();
		node["RawDescription"] = "changed";
		var changed = JsonSerializer.Deserialize<OrganizationEffectItemEntity>(node)!;
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state, evidence with { Items = [changed] }))
			.Should().Throw<InvalidDataException>().WithMessage("*sealed membership*");
	}

	[Theory]
	[InlineData("ExpectedAssignmentId")]
	[InlineData("TaxonomyRevisionId")]
	[InlineData("ApplyOperationId")]
	[InlineData("Store")]
	public void Effects_RejectsInvalidFrozenDependenciesEvenWithRecomputedMembership(string field)
	{
		var (state, evidence) = EffectState();
		var node = JsonSerializer.SerializeToNode(evidence.Items[0])!;
		node[field] = field == "Store" ? JsonSerializer.SerializeToNode("bad\0store")
			: JsonSerializer.SerializeToNode(field == "ApplyOperationId" ? Guid.Empty : Guid.NewGuid());
		var changed = JsonSerializer.Deserialize<OrganizationEffectItemEntity>(node)!;
		var tampered = evidence with
		{
			Items = [changed],
			Manifests = [PortableTaxonomyArchive.SealEffectManifest(evidence.Manifests[0].Manifest, [changed])]
		};
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state, tampered)).Should().Throw<InvalidDataException>();
	}

	[Fact]
	public void Effects_MultipleItemsHaveOrderIndependentMembershipAndSharedOperationNamespace()
	{
		var (state, evidence) = EffectState();
		var first = evidence.Items[0];
		var node = JsonSerializer.SerializeToNode(first)!;
		node["ItemId"] = Guid.NewGuid();
		node["ApplyOperationId"] = Guid.NewGuid();
		node["UndoOperationId"] = Guid.NewGuid();
		var second = JsonSerializer.Deserialize<OrganizationEffectItemEntity>(node)!;
		var manifest = evidence.Manifests[0].Manifest;
		var seal = PortableTaxonomyArchive.SealEffectManifest(manifest, [first, second]);
		PortableTaxonomyArchive.SealEffectManifest(manifest, [second, first]).Should().Be(seal);
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state,
			evidence with { Manifests = [seal], Items = [second, first] })).Should().NotThrow();
		node["UndoOperationId"] = first.ApplyOperationId;
		var collision = JsonSerializer.Deserialize<OrganizationEffectItemEntity>(node)!;
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state,
			evidence with { Manifests = [PortableTaxonomyArchive.SealEffectManifest(manifest, [first, collision])], Items = [first, collision] }))
			.Should().Throw<InvalidDataException>().WithMessage("*reservation collision*");
	}

	[Theory]
	[InlineData("1e2", "100.00", "1e2")]
	[InlineData("-0e1000000", "0.000", "0")]
	[InlineData("1.23456789012345678901234567890123456789e-50", "0.0000000000000000000000000000000000000000000000000123456789012345678901234567890123456789", "123456789012345678901234567890123456789e-88")]
	[InlineData("123456789012345678901234567890123456789.012345678901234567890", "123456789012345678901234567890123456789.01234567890123456789", "12345678901234567890123456789012345678901234567890123456789e-20")]
	[InlineData("-12.3400e+5", "-1234000", "-1234e3")]
	public void Effects_CanonicalNumbersUseExactUnboundedDecimalValues(string original, string equivalent, string expected)
	{
		PortableTaxonomyArchive.CanonicalEffectDecision("{\"n\":" + original + "}")
			.Should().Be("{\"n\":" + expected + "}")
			.And.Be(PortableTaxonomyArchive.CanonicalEffectDecision("{\"n\":" + equivalent + "}"));
	}

	[Fact]
	public void Effects_CanonicalNumbersKeepDistinctHighPrecisionValuesAndArrayOrder()
	{
		PortableTaxonomyArchive.CanonicalEffectDecision("{\"n\":123456789012345678901234567890123456789.1}")
			.Should().NotBe(PortableTaxonomyArchive.CanonicalEffectDecision("{\"n\":123456789012345678901234567890123456789.2}"));
		PortableTaxonomyArchive.CanonicalEffectDecision("{\"n\":[1,2]}")
			.Should().NotBe(PortableTaxonomyArchive.CanonicalEffectDecision("{\"n\":[2,1]}"));
	}

	[Fact]
	public void Effects_RejectsCrossPhaseReservationCollisionWithRetainedHistory()
	{
		var (state, evidence) = EffectState();
		state.HistoricalItems.Add(new() { UndoOperationId = evidence.Items[0].ApplyOperationId });
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state, evidence))
			.Should().Throw<InvalidDataException>().WithMessage("*reservation collision*");
	}

	[Fact]
	public void Effects_ValidatesApplyUndoAssignmentLineageAndObservedRevisions()
	{
		var (state, evidence) = EffectState();
		var item = evidence.Items[0];
		var manifest = evidence.Manifests[0].Manifest;
		var apply = new PurchaseClassificationEntity
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = item.ItemId,
			Actor = manifest.Actor,
			OperationId = item.ApplyOperationId,
			CategoryId = item.TargetCategoryId,
			TaxonomyRevisionId = item.TaxonomyRevisionId,
			RecordedAt = manifest.CapturedAt
		};
		var undo = new PurchaseClassificationEntity
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = item.ItemId,
			Actor = manifest.Actor,
			OperationId = item.UndoOperationId,
			PreviousAssignmentId = apply.Id,
			TaxonomyRevisionId = item.TaxonomyRevisionId,
			RecordedAt = manifest.CapturedAt
		};
		state.Assignments.AddRange([apply, undo]);
		evidence.Outcomes.AddRange(new[] { ("apply", apply.Id), ("undo", undo.Id) }.Select(x => new OrganizationEffectOutcomeEntity
		{
			ManifestId = manifest.Id,
			ItemId = item.ItemId,
			Phase = x.Item1,
			Disposition = "applied",
			Reason = "policy",
			AssignmentId = x.Id,
			ItemRevision = Guid.NewGuid(),
			ReceiptRevision = Guid.NewGuid(),
			RecordedAt = manifest.CapturedAt
		}));
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state, evidence)).Should().NotThrow();
		undo.PreviousAssignmentId = null;
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state, evidence))
			.Should().Throw<InvalidDataException>().WithMessage("*assignment lineage*");
		undo.PreviousAssignmentId = apply.Id;
		undo.CategoryId = item.TargetCategoryId;
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state, evidence))
			.Should().Throw<InvalidDataException>().WithMessage("*assignment lineage*");
	}

	[Fact]
	public void Effects_RejectsUndoWithoutAppliedResultAndUnrecordedRetainedAssignments()
	{
		var (state, evidence) = EffectState();
		var item = evidence.Items[0];
		evidence.Outcomes.Add(new() { ManifestId = item.ManifestId, ItemId = item.ItemId, Phase = "undo", Reason = "skip", RecordedAt = evidence.Manifests[0].Manifest.CapturedAt });
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state, evidence))
			.Should().Throw<InvalidDataException>().WithMessage("*undo lacks applied*");
		evidence.Outcomes.Clear();
		state.Assignments.Add(new() { Id = Guid.NewGuid(), OperationId = item.ApplyOperationId });
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state, evidence))
			.Should().Throw<InvalidDataException>().WithMessage("*retained assignment*");
	}
}
