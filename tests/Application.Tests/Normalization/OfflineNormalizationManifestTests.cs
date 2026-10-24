using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Normalization;
using FluentAssertions;
using Xunit;

namespace Application.Tests.Normalization;

public class OfflineNormalizationManifestTests
{
	private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
	private static OfflineNormalizationManifest Proposal() => new(1, new string('A', 64), 8, new string('B', 64),
		"revision", "proposer", Now.AddHours(-1), [], [], [], [], null);
	private static OfflineNormalizationManifest WithDecision()
	{
		var key = new NormalizationSourceKey("Shop", "Milk", null);
		return Proposal() with
		{
			Assignments = [new(Guid.NewGuid(), "item", Guid.NewGuid(), key,
			NormalizationCatalogPolicy.Hash(key), null, true, false, "Ambiguous package")]
		};
	}
	private static OfflineNormalizationManifest Reviewed()
	{
		var manifest = WithDecision();
		return manifest with { Review = new("reviewer", Now, [new(manifest.Assignments[0].DecisionId, "Source cannot distinguish sizes")]) };
	}

	[Fact]
	public void Read_Proposal_HasNoReviewAuthority()
	{
		string json = JsonSerializer.Serialize(Proposal(), OfflineNormalizationManifestPolicy.JsonOptions);
		var proposal = OfflineNormalizationManifestPolicy.Read(json, false, Now);
		proposal.Review.Should().BeNull();
		proposal.Assignments.Should().BeEmpty();
		Action apply = () => OfflineNormalizationManifestPolicy.Read(json, true, Now);
		apply.Should().Throw<InvalidDataException>();
	}

	[Fact]
	public void Read_CompleteIndependentReview_RetainsIndividualRationaleAndActor()
	{
		var expected = Reviewed();
		var result = OfflineNormalizationManifestPolicy.Read(JsonSerializer.Serialize(expected), true, Now);
		result.Review!.Decisions.Single().Should().Be(expected.Review!.Decisions.Single());
		OfflineNormalizationManifestPolicy.Actor(result, new string('C', 64)).Should()
			.Be("offline-agent:reviewer;manifest-sha256:" + new string('C', 64));
	}

	[Theory]
	[InlineData("duplicate")]
	[InlineData("nested-duplicate")]
	[InlineData("unknown")]
	[InlineData("null-list")]
	[InlineData("null-member")]
	[InlineData("missing")]
	[InlineData("truncated")]
	public void Read_MalformedOrAmbiguousJson_Rejects(string mutation)
	{
		string json = JsonSerializer.Serialize(Reviewed());
		var node = JsonNode.Parse(json)!.AsObject();
		switch (mutation)
		{
			case "duplicate": json = json.Insert(1, "\"ManifestVersion\":1,"); break;
			case "nested-duplicate": json = json.Replace("\"ReviewerIdentity\":", "\"ReviewerIdentity\":\"other\",\"ReviewerIdentity\":"); break;
			case "unknown": node["ApproveEverything"] = true; json = node.ToJsonString(); break;
			case "null-list": node["Assignments"] = null; json = node.ToJsonString(); break;
			case "null-member": node["Assignments"] = new JsonArray((JsonNode?)null); json = node.ToJsonString(); break;
			case "missing": node.Remove("ProposedBy"); json = node.ToJsonString(); break;
			case "truncated": json = json[..^5]; break;
		}
		Action read = () => OfflineNormalizationManifestPolicy.Read(json, true, Now);
		read.Should().Throw<Exception>().Where(e => e is InvalidDataException || e is JsonException);
	}

	[Theory]
	[InlineData("same-author")]
	[InlineData("missing-review")]
	[InlineData("blank-rationale")]
	[InlineData("extra-review")]
	[InlineData("duplicate-review")]
	[InlineData("future")]
	[InlineData("before-proposal")]
	public void Validate_InvalidReview_Rejects(string mutation)
	{
		var manifest = Reviewed();
		var review = manifest.Review!;
		review = mutation switch
		{
			"same-author" => review with { ReviewerIdentity = " PROPOSER " },
			"missing-review" => review with { Decisions = [] },
			"blank-rationale" => review with { Decisions = [new(manifest.Assignments[0].DecisionId, " ")] },
			"extra-review" => review with { Decisions = [.. review.Decisions, new(Guid.NewGuid(), "Not a decision")] },
			"duplicate-review" => review with { Decisions = [.. review.Decisions, review.Decisions[0]] },
			"future" => review with { ReviewedAt = Now.AddSeconds(1) },
			_ => review with { ReviewedAt = manifest.ProposedAt.AddSeconds(-1) }
		};
		Action validate = () => OfflineNormalizationManifestPolicy.Validate(manifest with { Review = review }, true, Now);
		validate.Should().Throw<InvalidDataException>();
	}

	[Theory]
	[InlineData("bad-hash")]
	[InlineData("unnormalized-source")]
	[InlineData("template-remember")]
	[InlineData("duplicate-assignment")]
	[InlineData("unresolved-assigned")]
	[InlineData("chain")]
	[InlineData("exclusion-no-reason")]
	[InlineData("lowercase-digest")]
	public void Validate_InvalidDecision_Rejects(string mutation)
	{
		var manifest = WithDecision();
		var assignment = manifest.Assignments[0];
		manifest = mutation switch
		{
			"bad-hash" => manifest with { Assignments = [assignment with { SourceHash = new string('C', 64) }] },
			"unnormalized-source" => manifest with { Assignments = [assignment with { Source = assignment.Source with { Description = " Milk " } }] },
			"template-remember" => manifest with { Assignments = [assignment with { EntityKind = "template", RememberSource = true }] },
			"duplicate-assignment" => manifest with { Assignments = [assignment, assignment with { DecisionId = Guid.NewGuid() }] },
			"unresolved-assigned" => manifest with { Unresolved = [new("item", assignment.EntityId, "Ambiguous")] },
			"exclusion-no-reason" => manifest with { Assignments = [assignment with { ExclusionReason = null }] },
			"lowercase-digest" => manifest with { SourceSha256 = new string('a', 64) },
			_ => Chain(manifest)
		};
		Action validate = () => OfflineNormalizationManifestPolicy.Validate(manifest, false, Now);
		validate.Should().Throw<InvalidDataException>();
	}

	private static OfflineNormalizationManifest Chain(OfflineNormalizationManifest manifest)
	{
		Guid first = Guid.NewGuid(), middle = Guid.NewGuid(), last = Guid.NewGuid();
		return manifest with { Consolidations = [new(Guid.NewGuid(), first, middle), new(Guid.NewGuid(), middle, last)] };
	}
}
