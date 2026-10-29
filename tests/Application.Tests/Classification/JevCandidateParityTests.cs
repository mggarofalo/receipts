using System.Security.Cryptography;
using System.Text.Json;
using Application.Classification;
using Application.Classification.Experimental;
using Receipts.JevCandidateParity;

namespace Application.Tests.Classification;

public class JevCandidateParityTests
{
	[Fact]
	public void EvaluatesActualCandidateWithAncestorExpansion()
	{
		CandidateParityRecord record = CandidateParity.Evaluate(Fixture());

		Assert.Equal("apple", record.Decision.CategoryId);
		Assert.Equal(new[] { "food", "apple" }, record.Decision.Path);
		Assert.Equal(new[] { "apple", "food" }, record.Plan.Plan.Paths.Select(path => path.CategoryId));
		Assert.Equal(SupportedCategoryCandidate.PurchaseEvidenceQuestion, record.Plan.PurchaseEvidenceQuestion);
		Assert.Equal(SupportedCategoryCandidate.EvidenceRule, record.Plan.EvidenceRule);
		Assert.Equal(SupportedCategoryCandidate.Version, record.Plan.PolicyVersion);
	}

	[Fact]
	public void FingerprintsExactDocumentedObjectsAndExcludeFixtureIdentity()
	{
		CandidateParityFixture fixture = Fixture();
		CandidateParityRecord record = CandidateParity.Evaluate(fixture);
		CandidateParityRecord renamed = CandidateParity.Evaluate(fixture with { Id = "other-fixture" });

		Assert.Equal(record.PlanSha256, renamed.PlanSha256);
		Assert.Equal(record.DecisionSha256, renamed.DecisionSha256);
		Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(
			JsonSerializer.SerializeToUtf8Bytes(record.Plan, CandidateParity.JsonOptions))), record.PlanSha256);
		Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(
			JsonSerializer.SerializeToUtf8Bytes(record.Decision, CandidateParity.JsonOptions))), record.DecisionSha256);
		CandidateParityRecord repeated = CandidateParity.Evaluate(fixture);
		Assert.Equal(record.PlanSha256, repeated.PlanSha256);
		Assert.Equal(record.DecisionSha256, repeated.DecisionSha256);
	}

	[Theory]
	[InlineData("description")]
	[InlineData("store")]
	[InlineData("product-code")]
	[InlineData("taxonomy")]
	[InlineData("revision")]
	[InlineData("shortlist")]
	public void PlanFingerprintCapturesEvidenceTaxonomyRevisionAndExpandedPaths(string change)
	{
		CandidateParityFixture fixture = Fixture();
		CandidateParityFixture changed = change switch
		{
			"description" => fixture with { Purchase = fixture.Purchase with { Description = "Fresh pears" } },
			"store" => fixture with { Purchase = fixture.Purchase with { Store = "Synthetic shop" } },
			"product-code" => fixture with { Purchase = fixture.Purchase with { ProductCode = "SYNTHETIC" } },
			"taxonomy" => fixture with { Taxonomy = [fixture.Taxonomy[0], fixture.Taxonomy[1] with { Definition = "Dessert apples" }] },
			"revision" => fixture with { TaxonomyRevision = "synthetic-v2" },
			_ => fixture with { ShortlistedIds = ["food"] }
		};

		Assert.NotEqual(CandidateParity.Evaluate(fixture).PlanSha256, CandidateParity.Evaluate(changed).PlanSha256);
	}

	[Fact]
	public void JudgmentChangesDecisionWithoutChangingPlan()
	{
		CandidateParityFixture fixture = Fixture();
		CandidateParityRecord selected = CandidateParity.Evaluate(fixture);
		CandidateParityRecord abstained = CandidateParity.Evaluate(fixture with
		{
			Judgment = fixture.Judgment with { UnlistedAlternativeProbability = .8 }
		});

		Assert.Equal(selected.PlanSha256, abstained.PlanSha256);
		Assert.NotEqual(selected.DecisionSha256, abstained.DecisionSha256);
		Assert.Equal("uncovered-alternatives", abstained.Decision.AbstentionReason);
		Assert.Null(abstained.Decision.Failure);
	}

	[Fact]
	public void InvalidTypedJudgmentRemainsAFailureRatherThanAnAbstention()
	{
		CandidateParityFixture fixture = Fixture();
		CandidateParityRecord result = CandidateParity.Evaluate(fixture with
		{
			Judgment = fixture.Judgment with { TaxonomyRevision = "stale" }
		});

		Assert.Equal("invalid-judgment", result.Decision.Failure);
		Assert.Null(result.Decision.AbstentionReason);
	}

	[Fact]
	public void CandidateRejectsUnknownShortlistWithoutInventingCandidates()
	{
		Assert.Throws<ArgumentException>(() => CandidateParity.Evaluate(Fixture() with { ShortlistedIds = ["missing"] }));
	}

	[Fact]
	public void CommandRoundTripsSyntheticFixtureAndRecordsAbstentionAsSuccess()
	{
		CandidateParityFixture fixture = Fixture();
		fixture = fixture with { Judgment = fixture.Judgment with { PurchaseEvidenceProbability = .1 } };
		WithFixture(JsonSerializer.Serialize(fixture, CandidateParity.JsonOptions), path =>
		{
			using StringWriter output = new();
			using StringWriter error = new();
			Assert.Equal(0, CandidateParityCommand.Run([path], output, error));
			using JsonDocument result = JsonDocument.Parse(output.ToString());
			Assert.Equal("no-purchase-evidence", result.RootElement.GetProperty("decision").GetProperty("abstentionReason").GetString());
			Assert.Equal("", error.ToString());
		});
	}

	[Theory]
	[InlineData("null")]
	[InlineData("{\"id\":\"PRIVATE-SYNTHETIC\",\"unexpected\":true}")]
	[InlineData("{\"id\":\"PRIVATE-SYNTHETIC\"}")]
	[InlineData("PRIVATE-SYNTHETIC malformed JSON")]
	public void CommandRejectsInvalidFixturesWithoutEchoingContents(string json)
	{
		WithFixture(json, path =>
		{
			using StringWriter output = new();
			using StringWriter error = new();
			Assert.Equal(2, CandidateParityCommand.Run([path], output, error));
			Assert.Equal("", output.ToString());
			Assert.DoesNotContain("PRIVATE-SYNTHETIC", error.ToString());
			Assert.Contains("Invalid or unreadable fixture", error.ToString());
		});
	}

	[Fact]
	public void CommandRejectsOversizedFixtureBeforeParsing()
	{
		WithFixture(new string(' ', CandidateParityCommand.MaximumFixtureBytes + 1), path =>
		{
			using StringWriter output = new();
			using StringWriter error = new();
			Assert.Equal(2, CandidateParityCommand.Run([path], output, error));
			Assert.Equal("", output.ToString());
		});
	}

	[Fact]
	public void CommandRejectsUnknownPropertyOnOtherwiseValidFixture()
	{
		System.Text.Json.Nodes.JsonObject fixture = System.Text.Json.Nodes.JsonNode.Parse(
			JsonSerializer.Serialize(Fixture(), CandidateParity.JsonOptions))!.AsObject();
		fixture["unexpected"] = true;
		WithFixture(fixture.ToJsonString(), path =>
		{
			using StringWriter output = new();
			using StringWriter error = new();
			Assert.Equal(2, CandidateParityCommand.Run([path], output, error));
			Assert.Equal("", output.ToString());
		});
	}

	[Theory]
	[InlineData("unlistedAlternativeProbability")]
	[InlineData("purchaseEvidenceProbability")]
	[InlineData("taxonomyRevision")]
	[InlineData("paths")]
	public void CommandRejectsOmittedJudgmentFieldsRatherThanUsingDefaultValues(string field)
	{
		System.Text.Json.Nodes.JsonObject fixture = System.Text.Json.Nodes.JsonNode.Parse(
			JsonSerializer.Serialize(Fixture(), CandidateParity.JsonOptions))!.AsObject();
		fixture["judgment"]!.AsObject().Remove(field);
		WithFixture(fixture.ToJsonString(), path =>
		{
			using StringWriter output = new();
			using StringWriter error = new();
			Assert.Equal(2, CandidateParityCommand.Run([path], output, error));
			Assert.Equal("", output.ToString());
		});
	}

	[Theory]
	[InlineData("supportProbability")]
	[InlineData("contextSufficiencyProbability")]
	public void CommandRejectsOmittedPerPathProbabilities(string field)
	{
		System.Text.Json.Nodes.JsonObject fixture = System.Text.Json.Nodes.JsonNode.Parse(
			JsonSerializer.Serialize(Fixture(), CandidateParity.JsonOptions))!.AsObject();
		fixture["judgment"]!["paths"]!["apple"]!.AsObject().Remove(field);
		WithFixture(fixture.ToJsonString(), path =>
		{
			using StringWriter output = new();
			using StringWriter error = new();
			Assert.Equal(2, CandidateParityCommand.Run([path], output, error));
			Assert.Equal("", output.ToString());
		});
	}

	private static void WithFixture(string json, Action<string> action)
	{
		string path = System.IO.Path.GetTempFileName();
		try
		{
			File.WriteAllText(path, json);
			action(path);
		}
		finally
		{
			File.Delete(path);
		}
	}

	private static CandidateParityFixture Fixture() => new("synthetic-apple", new("Fresh apples", null, null),
		"synthetic-v1", [new SuggestionCategory("food", "Food", "Edible goods", null),
			new SuggestionCategory("apple", "Apples", "Fresh apples", "food")], ["apple"],
		new("synthetic-v1", .95, .1, new Dictionary<string, CandidatePathJudgment>
		{
			["food"] = new(.95, .95),
			["apple"] = new(.95, .95)
		}));
}
