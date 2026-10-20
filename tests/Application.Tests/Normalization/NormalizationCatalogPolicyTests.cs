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
	[InlineData("unassigned", false, true)]
	[InlineData("automatic", false, true)]
	[InlineData("legacy_unknown", false, false)]
	[InlineData("legacy_unknown", true, true)]
	[InlineData("manual", true, false)]
	[InlineData("template", true, false)]
	[InlineData("reviewed", true, false)]
	[InlineData("mapped", true, false)]
	[InlineData("excluded", true, false)]
	[InlineData("future-unknown-origin", true, false)]
	public void Eligibility_ConsentOnlyReopensLegacyAssignments(string origin, bool includeLegacy, bool expected)
	{
		NormalizationCatalogPolicy.Classify(new(origin, true, true, false, false, false, false), includeLegacy).Eligible.Should().Be(expected);
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
