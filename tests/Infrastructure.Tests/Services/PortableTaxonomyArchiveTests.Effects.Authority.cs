using System.Text.Json;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	private static (PortableTaxonomyArchive.State History, PortableTaxonomyArchive.EffectEvidence Effects) EffectAuthorityState()
	{
		var (history, effects) = EffectState();
		var genesis = history.Revisions[0];
		var revision = new TaxonomyRevisionEntity
		{
			Id = Guid.NewGuid(),
			PreviousRevisionId = genesis.Id,
			Sequence = 1,
			Actor = "actor",
			OperationId = Guid.NewGuid(),
			Reason = "create",
			RecordedAt = genesis.RecordedAt
		};
		history.Revisions.Add(revision);
		history.Heads[0].RevisionId = revision.Id;
		history.Identities.Add(new() { Id = effects.Items[0].TargetCategoryId });
		history.Nodes.Clear();
		history.Nodes.Add(new() { RevisionId = revision.Id, CategoryId = effects.Items[0].TargetCategoryId, Name = "Target" });
		history.Operations.Add(new()
		{
			Actor = revision.Actor,
			OperationId = revision.OperationId,
			RevisionId = revision.Id,
			Reason = revision.Reason,
			RecordedAt = revision.RecordedAt,
			RequestHash = new string('A', 64)
		});
		var manifestJson = JsonSerializer.SerializeToNode(effects.Manifests[0].Manifest)!;
		manifestJson["TaxonomyRevisionId"] = revision.Id;
		var manifest = manifestJson.Deserialize<OrganizationEffectManifestEntity>()!;
		var itemJson = JsonSerializer.SerializeToNode(effects.Items[0])!;
		itemJson["TaxonomyRevisionId"] = revision.Id;
		var item = itemJson.Deserialize<OrganizationEffectItemEntity>()!;
		return (history, new([PortableTaxonomyArchive.SealEffectManifest(manifest, [item])], [item], []));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Effects_AuthorityCandidateAcceptsPendingAndSkippedEvidence(bool skipped)
	{
		var (history, effects) = EffectAuthorityState();
		if (skipped)
		{
			effects.Outcomes.Add(new()
			{
				ManifestId = effects.Items[0].ManifestId,
				ItemId = effects.Items[0].ItemId,
				Disposition = "skipped",
				Reason = "source-unavailable",
				RecordedAt = history.Revisions[^1].RecordedAt
			});
		}
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectAssignmentAuthorityCandidate(history, effects))
			.Should().NotThrow();
		history.Heads.Clear();
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectAssignmentAuthorityCandidate(history, effects))
			.Should().Throw<InvalidDataException>().WithMessage("*taxonomy head*");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Effects_AuthorityCandidateRejectsAppliedEvidenceEvenWithManualOperation(bool manualAuthority)
	{
		var (history, effects) = EffectAuthorityState();
		var item = effects.Items[0];
		var manifest = effects.Manifests[0].Manifest;
		var assignment = new PurchaseClassificationEntity
		{
			Id = Guid.NewGuid(),
			ReceiptItemId = item.ItemId,
			Sequence = 1,
			Actor = manifest.Actor,
			OperationId = item.ApplyOperationId,
			CategoryId = item.TargetCategoryId,
			TaxonomyRevisionId = item.TaxonomyRevisionId,
			RecordedAt = manifest.CapturedAt,
			Source = "manual",
			Reason = "policy"
		};
		history.Assignments.Add(assignment);
		history.Current.Add(new() { ReceiptItemId = item.ItemId, AssignmentId = assignment.Id });
		if (manualAuthority)
		{
			history.ClassificationOperations.Add(new()
			{
				Actor = manifest.Actor,
				OperationId = item.ApplyOperationId,
				AssignmentId = assignment.Id,
				RequestHash = manifest.RequestHash
			});
			FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(history)).Should().NotThrow();
		}
		effects.Outcomes.Add(new()
		{
			ManifestId = manifest.Id,
			ItemId = item.ItemId,
			Disposition = "applied",
			Reason = "policy",
			AssignmentId = assignment.Id,
			ItemRevision = item.ItemRevision,
			ReceiptRevision = item.ReceiptRevision,
			RecordedAt = manifest.CapturedAt
		});
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(history, effects)).Should().NotThrow();
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectAssignmentAuthorityCandidate(history, effects))
			.Should().Throw<InvalidDataException>().WithMessage(manualAuthority
				? "*versioned request and decision contract*" : "*Assignment lacks its authoritative operation*");
	}

	[Theory]
	[InlineData("ApplyOperationId")]
	[InlineData("UndoOperationId")]
	public void Effects_AuthorityCandidateRejectsBorrowedOrdinaryOperationAcrossActors(string phase)
	{
		var (history, effects) = EffectAuthorityState();
		var ordinary = new TaxonomyOperationEntity
		{
			Actor = "different-actor",
			OperationId = Guid.NewGuid(),
			RevisionId = history.Revisions[^1].Id,
			Reason = "no-op",
			RecordedAt = history.Revisions[^1].RecordedAt,
			RequestHash = new string('B', 64)
		};
		history.Operations.Add(ordinary);
		var json = JsonSerializer.SerializeToNode(effects.Items[0])!;
		json[phase] = ordinary.OperationId;
		var changed = json.Deserialize<OrganizationEffectItemEntity>()!;
		effects = effects with
		{
			Items = [changed],
			Manifests = [PortableTaxonomyArchive.SealEffectManifest(effects.Manifests[0].Manifest, [changed])]
		};
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectEvidence(history, effects)).Should().NotThrow();
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectAssignmentAuthorityCandidate(history, effects))
			.Should().Throw<InvalidDataException>().WithMessage("*collides with ordinary*");
	}
}
