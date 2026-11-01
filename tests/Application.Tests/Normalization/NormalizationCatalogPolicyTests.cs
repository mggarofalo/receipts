using Application.Normalization;
using FluentAssertions;

namespace Application.Tests.Normalization;

public sealed class NormalizationCatalogPolicyTests
{
	[Fact]
	public void SourceKey_TrimsOuterWhitespaceWithoutErasingProductOrStoreDistinctions()
	{
		var normalized = NormalizationCatalogPolicy.Normalize(new(" Store A ", " BF  FRANKS 8CT ", " 0123 "));
		normalized.Should().Be(new NormalizationSourceKey("Store A", "BF  FRANKS 8CT", "0123"));
		var original = NormalizationCatalogPolicy.Hash(normalized);
		foreach (var different in new[]
		{
			normalized with { Location = "Store B" },
			normalized with { Location = "store A" },
			normalized with { Description = "bf  franks 8ct" },
			normalized with { Description = "BF FRANKS 8CT" },
			normalized with { Description = "BF  FRANKS 16CT" },
			normalized with { ItemCode = "123" },
			normalized with { ItemCode = null },
		})
		{
			NormalizationCatalogPolicy.Hash(different).Should().NotBe(original);
		}
		NormalizationCatalogPolicy.Hash(new(" Store A ", " BF  FRANKS 8CT ", " 0123 ")).Should().Be(original);
	}

	[Fact]
	public void SourceKey_TrimsOnlySpacesSoTabsAndNonBreakingSpacesSurvive()
	{
		// SourceHash must not change (RECEIPTS-1100): the source key trims spaces only, to match PostgreSQL
		// btrim, and this golden hash was captured from main before the description normaliser consolidation.
		var raw = new NormalizationSourceKey(" Store A ", "\t BF  FRANKS 8CT ", " 0123 ");

		var normalized = NormalizationCatalogPolicy.Normalize(raw);

		normalized.Description.Should().Be("\t BF  FRANKS 8CT");
		NormalizationCatalogPolicy.Hash(raw).Should().Be("55017C0602445D906A3C4200C3BAE980569067F8C99D07867531244E4EFA9C5A");
		NormalizationCatalogPolicy.Normalize(new("Store", "\u00A0Milk\u00A0", null)).Description.Should().Be("\u00A0Milk\u00A0");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("  ")]
	public void MissingCodesShareTheAbsentScopeWithoutMatchingAPresentCode(string? code)
	{
		var source = new NormalizationSourceKey("Store", "Milk", code);
		NormalizationCatalogPolicy.Normalize(source).ItemCode.Should().BeNull();
		NormalizationCatalogPolicy.Hash(source).Should().Be(NormalizationCatalogPolicy.Hash(source with { ItemCode = null }));
		NormalizationCatalogPolicy.Hash(source).Should().NotBe(NormalizationCatalogPolicy.Hash(source with { ItemCode = "0" }));
	}

	[Theory]
	[InlineData("manual")]
	[InlineData("template")]
	[InlineData("reviewed")]
	[InlineData("mapped")]
	[InlineData("legacy_unknown")]
	[InlineData("future-unknown-origin")]
	public void UnverifiedLinkedHistoryRequiresExplicitLegacyConsentRegardlessOfOrigin(string origin)
	{
		var facts = new CatalogAssignmentFacts(origin, true, true, false, false, origin == "template", false, false, true);
		NormalizationCatalogPolicy.Classify(facts, false).Eligible.Should().BeFalse();
		NormalizationCatalogPolicy.Classify(facts, true).Eligible.Should().BeTrue();
		NormalizationCatalogPolicy.Classify(facts with { HasReviewedEvidence = true }, true).Eligible.Should().BeFalse();
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void EmptyDescriptionsCannotBecomeReusableRules(string description)
	{
		Action normalize = () => NormalizationCatalogPolicy.Normalize(new("Store", description, null));
		normalize.Should().Throw<NormalizationCatalogException>()
			.Which.Code.Should().Be("invalid_source");
	}
}
