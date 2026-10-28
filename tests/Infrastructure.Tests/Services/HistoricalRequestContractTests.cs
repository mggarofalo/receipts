using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Normalization;
using Application.Taxonomy;
using FluentAssertions;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

public sealed class HistoricalRequestContractTests
{
	[Fact]
	public void ManualV0PreservesExistingHashAndCanonicalGroupOrder()
	{
		var request = new HistoricalRequestContract.Request(0, "manual", "actor",
				Guid.Parse("11111111-1111-1111-1111-111111111111"), "reason",
				[new(new("B", "Bread", "X"), Guid.Parse("22222222-2222-2222-2222-222222222222")),
						 new(new("A", "Apple", null), null)], null);
		HistoricalRequestContract.RequestHash(request).Should().Be("7623B8BEAAE3AA0859BB10EEE37DBA5D959BA0BDB015AEEE9A5E8D9ECA582FBF");
		HistoricalRequestContract.RequestHash(request with { Groups = request.Groups.Reverse().ToArray() })
				.Should().Be(HistoricalRequestContract.RequestHash(request));
		FluentActions.Invoking(() => HistoricalRequestContract.RequestHash(request with { Decisions = [] }))
				.Should().Throw<InvalidDataException>();
	}

	[Fact]
	public void AutomaticV1FixedVectorIsIndependentOfGroupAndDecisionOrder()
	{
		HistoricalGroupKey first = new("A", "Apple", null), second = new("B", "Bread", "X");
		Guid firstCategory = Guid.Parse("22222222-2222-2222-2222-222222222222");
		Guid secondCategory = Guid.Parse("33333333-3333-3333-3333-333333333333");
		var request = new HistoricalRequestContract.Request(1, "approved-mapping", "actor",
				Guid.Parse("11111111-1111-1111-1111-111111111111"), "reason",
				[new(first, firstCategory), new(second, secondCategory)],
				[new(1, "approved-exact-mapping", first, firstCategory,
								Guid.Parse("44444444-4444-4444-4444-444444444444"), Guid.Parse("55555555-5555-5555-5555-555555555555"),
								"23B8CAB6612E9A5901CC4777325B766A0769EE600EB0766E8BDCB37DEFACB5F4"),
						 new(1, "approved-exact-mapping", second, secondCategory,
								Guid.Parse("66666666-6666-6666-6666-666666666666"), Guid.Parse("77777777-7777-7777-7777-777777777777"),
								"EEE4B6D1ACEAE20B227B60C0AA0ABA9E7A77E567F3DBB5949EE33BDE4BC9E3CC")]);
		const string expected = "5033FE6D2967FDDCDB08A20C4736E8105D22569ED4D7900A889E78984000A00A";
		HistoricalRequestContract.RequestHash(request).Should().Be(expected);
		HistoricalRequestContract.RequestHash(request with { Groups = request.Groups.Reverse().ToArray() }).Should().Be(expected);
		HistoricalRequestContract.RequestHash(request with { Decisions = request.Decisions!.Reverse().ToArray() }).Should().Be(expected);
	}

	[Fact]
	public void AutomaticHashBindsEvidenceAndRejectsMismatchedClaims()
	{
		var request = Automatic();
		var decision = request.Decisions![0];
		string hash = HistoricalRequestContract.RequestHash(request);
		HistoricalRequestContract.RequestHash(request with { Decisions = [decision with { MappingRevisionId = Guid.NewGuid() }] }).Should().NotBe(hash);
		HistoricalRequestContract.RequestHash(request with { Decisions = [decision with { ApprovalTaxonomyRevisionId = Guid.NewGuid() }] }).Should().NotBe(hash);
		HistoricalRequestContract.MappingDecision[] invalid =
		[
				decision with { Version = 0 }, decision with { Kind = "manual" },
						decision with { MappingRevisionId = Guid.Empty }, decision with { ApprovalTaxonomyRevisionId = Guid.Empty },
						decision with { TargetCategoryId = Guid.Empty }, decision with { TargetCategoryId = Guid.NewGuid() },
						decision with { SourceFingerprint = new string('A', 64) }, decision with { SourceFingerprint = decision.SourceFingerprint.ToLowerInvariant() },
						decision with { Group = decision.Group with { RawDescription = "Different" } }
		];
		foreach (var value in invalid)
		{
			FluentActions.Invoking(() => HistoricalRequestContract.RequestHash(request with { Decisions = [value] }))
					.Should().Throw<InvalidDataException>();
		}
		FluentActions.Invoking(() => HistoricalRequestContract.RequestHash(request with { Decisions = [] })).Should().Throw<InvalidDataException>();
		FluentActions.Invoking(() => HistoricalRequestContract.RequestHash(request with { Version = 2 })).Should().Throw<InvalidDataException>();
	}

	[Fact]
	public void DecisionParsingRequiresExactFieldsAndValidEvidence()
	{
		var decision = Automatic().Decisions![0];
		string json = JsonSerializer.Serialize(decision);
		HistoricalRequestContract.ReadDecision(json).Should().Be(decision);
		JsonObject missing = JsonNode.Parse(json)!.AsObject();
		missing.Remove("ApprovalTaxonomyRevisionId");
		string[] invalid = [missing.ToJsonString(), json[..^1] + ",\"Extra\":1}", json[..^1] + ",\"Version\":1}", "null"];
		foreach (string value in invalid)
		{
			FluentActions.Invoking(() => HistoricalRequestContract.ReadDecision(value)).Should().Throw<InvalidDataException>();
		}
	}

	private static HistoricalRequestContract.Request Automatic()
	{
		HistoricalGroupKey group = new("Store", "Item", null);
		string fingerprint = NormalizationCatalogPolicy.Hash(NormalizationCatalogPolicy.Normalize(new(group.Store, group.RawDescription, group.ItemCode)));
		Guid category = Guid.NewGuid();
		return new(1, "approved-mapping", "actor", Guid.NewGuid(), "reason", [new(group, category)],
				[new(1, "approved-exact-mapping", group, category, Guid.NewGuid(), Guid.NewGuid(), fingerprint)]);
	}
}
