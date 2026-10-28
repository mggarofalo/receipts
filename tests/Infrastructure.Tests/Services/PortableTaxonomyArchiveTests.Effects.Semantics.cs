using System.Security.Cryptography;
using System.Text.Json;
using Application.Normalization;
using Application.Taxonomy;
using Domain.Taxonomy;
using FluentAssertions;
using Infrastructure.Entities.Taxonomy;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

public sealed partial class PortableTaxonomyArchiveTests
{
	private static (PortableTaxonomyArchive.State History, OrganizationEffectManifestEntity Manifest,
		OrganizationEffectItemEntity Item, OrganizationEffectContractV1.ItemRequest Request) EffectMappingClaim(bool ancestors = false)
	{
		var (history, effects) = EffectAuthorityState();
		var manifest = effects.Manifests[0].Manifest;
		var item = effects.Items[0];
		Guid root = Guid.NewGuid(), parent = Guid.NewGuid();
		if (ancestors)
		{
			history.Identities.AddRange([new() { Id = root }, new() { Id = parent }]);
			history.Nodes[0].ParentId = parent;
			history.Nodes.AddRange([
				new() { RevisionId = item.TaxonomyRevisionId, CategoryId = parent, ParentId = root, Name = "Parent", Definition = "Parent meaning" },
				new() { RevisionId = item.TaxonomyRevisionId, CategoryId = root, Name = "Root", Definition = "Root meaning" }
			]);
		}
		var source = NormalizationCatalogPolicy.Normalize(new(item.Store, item.RawDescription, item.ItemCode));
		string fingerprint = NormalizationCatalogPolicy.Hash(source);
		var command = new CategoryMappingCommand(source, fingerprint, null, item.TaxonomyRevisionId,
			item.TargetCategoryId, Guid.NewGuid(), "Synthetic approval");
		var mapping = new CategoryMappingRevisionEntity
		{
			Id = Guid.NewGuid(),
			Location = source.Location,
			Description = source.Description,
			ItemCode = source.ItemCode,
			SourceFingerprint = fingerprint,
			CategoryId = item.TargetCategoryId,
			TaxonomyRevisionId = item.TaxonomyRevisionId,
			Actor = "synthetic-approver",
			OperationId = command.OperationId,
			Reason = command.Reason,
			RecordedAt = manifest.CapturedAt,
			RequestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command))),
			SemanticFingerprint = CategoryMappingPolicy.SemanticFingerprint(history.Nodes
				.Select(n => new TaxonomyNode(n.CategoryId, n.ParentId, n.Name, n.Definition, n.Archived)).ToList(), item.TargetCategoryId)
		};
		history.MappingRevisions.Add(mapping);
		history.MappingHeads.Add(new() { SourceFingerprint = fingerprint, RevisionId = mapping.Id });
		var decision = new OrganizationEffectContractV1.MappingEvidence(1, "approved-exact-mapping", mapping.Id,
			mapping.TaxonomyRevisionId, fingerprint);
		item = ChangeClaimField(item, "DecisionEvidenceJson", JsonSerializer.Serialize(decision));
		var request = new OrganizationEffectContractV1.ItemRequest(1, manifest.Id, manifest.Actor, manifest.PolicyVersion,
			manifest.CapturedAt, new(item.ReceiptId, item.ItemId, item.Store, item.RawDescription, item.ItemCode,
				item.ReceiptRevision, item.ItemRevision), item.ExpectedAssignmentId,
			new(item.TargetCategoryId, item.TaxonomyRevisionId, ancestors ? [item.TargetCategoryId, parent, root] : [item.TargetCategoryId]),
			item.ApplyOperationId, item.UndoOperationId, decision);
		return (history, manifest, item, request);
	}

	private static T ChangeClaimField<T, TValue>(T value, string field, TValue replacement)
	{
		var json = JsonSerializer.SerializeToNode(value)!;
		json[field] = JsonSerializer.SerializeToNode(replacement);
		return json.Deserialize<T>()!;
	}

	[Fact]
	public void Effects_MappingClaimAcceptsExactNormalizedEvidenceWithoutAuthorizingApply()
	{
		var (history, manifest, item, request) = EffectMappingClaim();
		request.Source.Store.Should().Be(" Store ");
		history.MappingRevisions[0].Location.Should().Be("Store");
		FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectMappingClaimCandidate(history, manifest, item, request))
			.Should().NotThrow();
		PortableBackupFormat.CurrentVersion.Should().Be(13);
	}

	[Fact]
	public void Effects_MappingClaimRejectsEveryFrozenRequestMismatch()
	{
		var (history, manifest, item, request) = EffectMappingClaim();
		OrganizationEffectContractV1.ItemRequest[] mutations =
		[
			request with { ManifestId = Guid.NewGuid() }, request with { Actor = "other" },
			request with { PolicyVersion = "other" }, request with { CapturedAt = request.CapturedAt.AddTicks(10) },
			request with { Source = request.Source with { ReceiptId = Guid.NewGuid() } },
			request with { Source = request.Source with { ItemId = Guid.NewGuid() } },
			request with { Source = request.Source with { Store = "Store" } },
			request with { Source = request.Source with { RawDescription = "other" } },
			request with { Source = request.Source with { ItemCode = "" } },
			request with { Source = request.Source with { ReceiptRevision = Guid.NewGuid() } },
			request with { Source = request.Source with { ItemRevision = Guid.NewGuid() } },
			request with { ExpectedAssignmentId = Guid.NewGuid() },
			request with { ApplyOperationId = Guid.NewGuid() }, request with { UndoOperationId = Guid.NewGuid() },
			request with { Target = new(Guid.NewGuid(), item.TaxonomyRevisionId, [Guid.NewGuid()]) },
			request with { Target = request.Target with { TaxonomyRevisionId = Guid.NewGuid() } },
			request with { Target = request.Target with { Path = [item.TargetCategoryId, Guid.NewGuid()] } },
			request with { Decision = request.Decision with { MappingRevisionId = Guid.NewGuid() } }
		];
		foreach (var mutation in mutations)
		{
			FluentActions.Invoking(() => PortableTaxonomyArchive.ValidateEffectMappingClaimCandidate(history, manifest, item, mutation))
				.Should().Throw<InvalidDataException>();
		}
	}

	[Theory]
	[InlineData("missing-mapping")]
	[InlineData("approval-taxonomy")]
	[InlineData("fingerprint")]
	[InlineData("source-tuple")]
	[InlineData("future-approval")]
	[InlineData("opaque-decision")]
	[InlineData("forged-command")]
	[InlineData("forged-semantics")]
	[InlineData("revocation")]
	public void Effects_MappingClaimRejectsSelfAssertedOrMismatchedEvidence(string mutation)
	{
		var (history, manifest, item, request) = EffectMappingClaim();
		switch (mutation)
		{
			case "missing-mapping": request = request with { Decision = request.Decision with { MappingRevisionId = Guid.NewGuid() } }; break;
			case "approval-taxonomy": request = request with { Decision = request.Decision with { ApprovalTaxonomyRevisionId = history.Revisions[0].Id } }; break;
			case "fingerprint": request = request with { Decision = request.Decision with { SourceFingerprint = new string('B', 64) } }; break;
			case "source-tuple":
				item = ChangeClaimField(item, "RawDescription", "different");
				request = request with { Source = request.Source with { RawDescription = "different" } }; break;
			case "future-approval": history.MappingRevisions[0].RecordedAt = manifest.CapturedAt.AddTicks(10); break;
			case "forged-command": history.MappingRevisions[0].RequestHash = new string('B', 64); break;
			case "forged-semantics": history.MappingRevisions[0].SemanticFingerprint = new string('B', 64); break;
			case "revocation":
				var approval = history.MappingRevisions[0];
				var source = new NormalizationSourceKey(approval.Location, approval.Description, approval.ItemCode);
				var command = new CategoryMappingCommand(source, approval.SourceFingerprint, approval.Id,
					approval.TaxonomyRevisionId, null, Guid.NewGuid(), "Synthetic revoke");
				var revocation = new CategoryMappingRevisionEntity
				{
					Id = Guid.NewGuid(),
					PreviousRevisionId = approval.Id,
					Location = approval.Location,
					Description = approval.Description,
					ItemCode = approval.ItemCode,
					SourceFingerprint = approval.SourceFingerprint,
					TaxonomyRevisionId = approval.TaxonomyRevisionId,
					Actor = approval.Actor,
					RecordedAt = approval.RecordedAt,
					OperationId = command.OperationId,
					Reason = command.Reason,
					RequestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)))
				};
				history.MappingRevisions.Add(revocation);
				history.MappingHeads[0].RevisionId = revocation.Id;
				request = request with { Decision = request.Decision with { MappingRevisionId = revocation.Id } }; break;
		}
		item = ChangeClaimField(item, "DecisionEvidenceJson", mutation == "opaque-decision" ? "{}" : JsonSerializer.Serialize(request.Decision));
		Action validate = () => PortableTaxonomyArchive.ValidateEffectMappingClaimCandidate(history, manifest, item, request);
		if (mutation == "opaque-decision")
		{
			validate.Should().Throw<JsonException>().WithMessage("*missing required properties*");
		}
		else
		{
			string message = mutation switch
			{
				"forged-command" => "Invalid mapping request hash.",
				"forged-semantics" => "Invalid mapping semantic fingerprint.",
				"fingerprint" or "source-tuple" => "Effect mapping source tuple or fingerprint differs.",
				_ => "Effect mapping reference lacks matching approval evidence."
			};
			validate.Should().Throw<InvalidDataException>().WithMessage(message);
		}
	}

	[Theory]
	[InlineData("ordered", true)]
	[InlineData("truncated", false)]
	[InlineData("reversed-ancestors", false)]
	[InlineData("reversed-all", false)]
	public void Effects_MappingClaimRequiresCompleteCategoryToRootPath(string change, bool accepted)
	{
		var (history, manifest, item, request) = EffectMappingClaim(ancestors: true);
		request.Target.Path.Should().HaveCount(3);
		Guid[] path = request.Target.Path;
		request = request with
		{
			Target = request.Target with
			{
				Path = change switch
				{
					"truncated" => path[..2],
					"reversed-ancestors" => [path[0], path[2], path[1]],
					"reversed-all" => path.Reverse().ToArray(),
					_ => path
				}
			}
		};
		Action validate = () => PortableTaxonomyArchive.ValidateEffectMappingClaimCandidate(history, manifest, item, request);
		if (accepted) { validate.Should().NotThrow(); }
		else
		{
			validate.Should().Throw<InvalidDataException>().WithMessage(change == "reversed-all"
				? "Effect category and path differ." : "Effect target path differs from immutable taxonomy.");
		}
	}

	[Theory]
	[InlineData("rename", true)]
	[InlineData("definition", false)]
	[InlineData("archive", false)]
	[InlineData("reparent", false)]
	public void Effects_MappingClaimRechecksAncestorSemanticsInValidLaterSnapshot(string change, bool accepted)
	{
		var (history, manifest, item, request) = EffectMappingClaim(ancestors: true);
		Guid parent = request.Target.Path[1], root = request.Target.Path[2];
		var prior = history.Revisions[^1];
		var revision = new TaxonomyRevisionEntity
		{
			Id = Guid.NewGuid(),
			PreviousRevisionId = prior.Id,
			Sequence = prior.Sequence + 1,
			Actor = "synthetic-editor",
			OperationId = Guid.NewGuid(),
			Reason = "Synthetic ancestor edit",
			RecordedAt = manifest.CapturedAt
		};
		history.Revisions.Add(revision);
		history.Heads[0].RevisionId = revision.Id;
		history.Operations.Add(new()
		{
			Actor = revision.Actor,
			OperationId = revision.OperationId,
			RevisionId = revision.Id,
			Reason = revision.Reason,
			RecordedAt = revision.RecordedAt,
			RequestHash = new string('C', 64)
		});
		foreach (var node in history.Nodes.Where(n => n.RevisionId == prior.Id).ToArray())
		{
			history.Nodes.Add(new()
			{
				RevisionId = revision.Id,
				CategoryId = node.CategoryId,
				ParentId = change == "reparent" && node.CategoryId == parent ? null : node.ParentId,
				Name = change == "rename" && node.CategoryId == parent ? "Renamed parent" : node.Name,
				Definition = change == "definition" && node.CategoryId == root ? "Changed ancestor meaning" : node.Definition,
				Archived = change == "archive" && node.CategoryId != root
			});
		}
		manifest = ChangeClaimField(manifest, "TaxonomyRevisionId", revision.Id);
		item = ChangeClaimField(item, "TaxonomyRevisionId", revision.Id);
		request = request with
		{
			Target = request.Target with
			{
				TaxonomyRevisionId = revision.Id,
				Path = change == "reparent" ? [item.TargetCategoryId, parent] : request.Target.Path
			}
		};
		// Reject changed meaning after ordinary validation succeeds, not because the synthetic tree is malformed.
		FluentActions.Invoking(() => PortableTaxonomyArchive.Validate(history)).Should().NotThrow();
		Action validate = () => PortableTaxonomyArchive.ValidateEffectMappingClaimCandidate(history, manifest, item, request);
		if (accepted) { validate.Should().NotThrow(); }
		else { validate.Should().Throw<InvalidDataException>().WithMessage("Effect target semantics differ from approved mapping."); }
	}

	[Theory]
	[InlineData("rename", true)]
	[InlineData("definition", false)]
	[InlineData("archived", false)]
	[InlineData("future-taxonomy", false)]
	public void Effects_MappingClaimChecksTargetSnapshotAgainstOriginalApproval(string change, bool accepted)
	{
		var (history, manifest, item, request) = EffectMappingClaim();
		var prior = history.Revisions[^1];
		var revision = new TaxonomyRevisionEntity
		{
			Id = Guid.NewGuid(),
			PreviousRevisionId = prior.Id,
			Sequence = prior.Sequence + 1,
			Actor = "synthetic-editor",
			OperationId = Guid.NewGuid(),
			Reason = "Synthetic edit",
			RecordedAt = change == "future-taxonomy" ? manifest.CapturedAt.AddTicks(10) : manifest.CapturedAt
		};
		history.Revisions.Add(revision);
		history.Heads[0].RevisionId = revision.Id;
		history.Operations.Add(new()
		{
			Actor = revision.Actor,
			OperationId = revision.OperationId,
			RevisionId = revision.Id,
			Reason = revision.Reason,
			RecordedAt = revision.RecordedAt,
			RequestHash = new string('C', 64)
		});
		history.Nodes.Add(new()
		{
			RevisionId = revision.Id,
			CategoryId = item.TargetCategoryId,
			Name = "Renamed target",
			Definition = change == "definition" ? "Different meaning" : null,
			Archived = change == "archived"
		});
		manifest = ChangeClaimField(manifest, "TaxonomyRevisionId", revision.Id);
		item = ChangeClaimField(item, "TaxonomyRevisionId", revision.Id);
		request = request with { Target = request.Target with { TaxonomyRevisionId = revision.Id } };
		Action validate = () => PortableTaxonomyArchive.ValidateEffectMappingClaimCandidate(history, manifest, item, request);
		if (accepted) { validate.Should().NotThrow(); }
		else { validate.Should().Throw<InvalidDataException>(); }
	}
}
