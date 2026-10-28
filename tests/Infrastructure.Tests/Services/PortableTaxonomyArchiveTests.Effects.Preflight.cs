using System.Text.Json;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	[Fact]
	public void Effects_DestinationRetainsOmittedManifestsAndOlderArchiveEvidence()
	{
		var (state, existing) = EffectState();
		existing.Outcomes.Add(SkippedEffect(existing));
		var older = PortableTaxonomyArchive.ValidateEffectDestination(state, existing, state, null);
		older.Manifests.Should().Equal(existing.Manifests);
		older.Items.Should().Equal(existing.Items);
		older.Outcomes.Should().Equal(existing.Outcomes);
		var omitted = PortableTaxonomyArchive.ValidateEffectDestination(state, existing, state, new([], [], []));
		omitted.Outcomes.Should().Equal(existing.Outcomes);
	}

	[Theory]
	[InlineData("actor")]
	[InlineData("member")]
	[InlineData("expand")]
	public void Effects_DestinationRejectsRewrittenSharedManifestEvenWhenResealed(string mutation)
	{
		var (state, existing) = EffectState();
		var incoming = CloneEffects(existing);
		if (mutation == "actor")
		{
			var node = JsonSerializer.SerializeToNode(incoming.Manifests[0].Manifest)!;
			node["Actor"] = "another actor";
			incoming.Manifests[0] = PortableTaxonomyArchive.SealEffectManifest(
				JsonSerializer.Deserialize<OrganizationEffectManifestEntity>(node)!, incoming.Items);
		}
		else
		{
			var node = JsonSerializer.SerializeToNode(incoming.Items[0])!;
			node[mutation == "member" ? "RawDescription" : "ItemId"] = mutation == "member"
				? JsonSerializer.SerializeToNode("changed") : JsonSerializer.SerializeToNode(Guid.NewGuid());
			if (mutation == "expand")
			{
				node["ApplyOperationId"] = Guid.NewGuid();
				node["UndoOperationId"] = Guid.NewGuid();
				incoming.Items.Add(JsonSerializer.Deserialize<OrganizationEffectItemEntity>(node)!);
			}
			else { incoming.Items[0] = JsonSerializer.Deserialize<OrganizationEffectItemEntity>(node)!; }
			incoming.Manifests[0] = PortableTaxonomyArchive.SealEffectManifest(incoming.Manifests[0].Manifest, incoming.Items);
		}
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(state, incoming)).Should().NotThrow();
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectDestination(state, existing, state, incoming))
			.Should().Throw<InvalidDataException>().WithMessage("*immutable effect manifest*");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Effects_DestinationRejectsLostOrChangedTerminalOutcome(bool rewrite)
	{
		var (state, existing) = EffectState();
		existing.Outcomes.Add(SkippedEffect(existing));
		var incoming = CloneEffects(existing);
		if (rewrite)
		{
			var node = JsonSerializer.SerializeToNode(incoming.Outcomes[0])!;
			node["Reason"] = "another terminal reason";
			incoming.Outcomes[0] = JsonSerializer.Deserialize<OrganizationEffectOutcomeEntity>(node)!;
		}
		else { incoming.Outcomes.Clear(); }
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectDestination(state, existing, state, incoming))
			.Should().Throw<InvalidDataException>().WithMessage("*terminal effect outcome*");
	}

	[Fact]
	public void Effects_DestinationAllowsAdditionalTerminalOutcomeAndCanonicalDecisionRendering()
	{
		var (state, existing) = EffectState();
		var incoming = CloneEffects(existing);
		var node = JsonSerializer.SerializeToNode(incoming.Items[0])!;
		node["DecisionEvidenceJson"] = "{\"a\":{\"yes\":true},\"z\":[1.00,null]}";
		incoming.Items[0] = JsonSerializer.Deserialize<OrganizationEffectItemEntity>(node)!;
		incoming.Outcomes.Add(SkippedEffect(incoming));
		var retained = PortableTaxonomyArchive.ValidateEffectDestination(state, existing, state, incoming);
		retained.Outcomes.Should().ContainSingle().Which.Should().Be(incoming.Outcomes[0]);
		existing.Outcomes.Should().BeEmpty();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Effects_DestinationRejectsReservationsAcrossRetainedAndIncomingHistories(bool effectIsRetained)
	{
		var (state, evidence) = EffectState();
		var history = state with { HistoricalItems = [new() { UndoOperationId = evidence.Items[0].ApplyOperationId }] };
		FluentActions.Invoking(() => effectIsRetained
			? PortableTaxonomyArchive.ValidateEffectDestination(state, evidence, history, null)
			: PortableTaxonomyArchive.ValidateEffectDestination(history, new([], [], []), state, evidence))
			.Should().Throw<InvalidDataException>().WithMessage("*reservation collision*");
	}

	[Fact]
	public void Effects_DestinationRequiresOlderArchiveToRetainFrozenAssignmentDependencies()
	{
		var (state, existing) = EffectState();
		Guid prior = Guid.NewGuid();
		state.Assignments.Add(new()
		{
			Id = prior,
			ReceiptItemId = existing.Items[0].ItemId,
			RecordedAt = existing.Manifests[0].Manifest.CapturedAt
		});
		var item = JsonSerializer.SerializeToNode(existing.Items[0])!;
		item["ExpectedAssignmentId"] = prior;
		existing.Items[0] = JsonSerializer.Deserialize<OrganizationEffectItemEntity>(item)!;
		existing.Manifests[0] = PortableTaxonomyArchive.SealEffectManifest(existing.Manifests[0].Manifest, existing.Items);
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectDestination(state, existing,
			state with { Assignments = [] }, null)).Should().Throw<InvalidDataException>().WithMessage("*prior assignment*");
	}

	[Fact]
	public void Effects_DestinationRejectsCrossManifestReservationCollisionInRetainedUnion()
	{
		var (state, existing) = EffectState();
		var incoming = CloneEffects(existing);
		var manifest = JsonSerializer.SerializeToNode(incoming.Manifests[0].Manifest)!;
		Guid id = Guid.NewGuid();
		manifest["Id"] = id;
		var item = JsonSerializer.SerializeToNode(incoming.Items[0])!;
		item["ManifestId"] = id;
		incoming.Items[0] = JsonSerializer.Deserialize<OrganizationEffectItemEntity>(item)!;
		incoming.Manifests[0] = PortableTaxonomyArchive.SealEffectManifest(
			JsonSerializer.Deserialize<OrganizationEffectManifestEntity>(manifest)!, incoming.Items);
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectDestination(state, existing, state, incoming))
			.Should().Throw<InvalidDataException>().WithMessage("*reservation collision*");
	}

	private static PortableTaxonomyArchive.EffectEvidence CloneEffects(PortableTaxonomyArchive.EffectEvidence evidence) =>
		PortableTaxonomyArchive.ReadEffectEvidence(PortableTaxonomyArchive.WriteEffectEvidence(evidence));

	private static OrganizationEffectOutcomeEntity SkippedEffect(PortableTaxonomyArchive.EffectEvidence evidence) => new()
	{
		ManifestId = evidence.Items[0].ManifestId,
		ItemId = evidence.Items[0].ItemId,
		Reason = "source changed",
		RecordedAt = evidence.Manifests[0].Manifest.CapturedAt
	};
}
