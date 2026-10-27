using Application.Normalization;
using FluentAssertions;

namespace Application.Tests.Normalization;

public sealed class ReceiptSuggestionEvidenceTests
{
	[Theory]
	[InlineData("Organic whole milk", "Organic whole chicken", false)]
	[InlineData("Whole milk", "Milk", true)]
	[InlineData("Baked beans 12 oz", "Rubber mallet 12 inch", false)]
	[InlineData("Baked beans 12 oz", "Baked beans", true)]
	[InlineData("Baked beans", "Baked potato", false)]
	[InlineData("Acme beans", "Acme mallet", false)]
	[InlineData("Organic whole milk", "Milk", true)]
	public void IsUseful_RequiresProductEvidenceBeyondSharedModifiers(string source, string target, bool expected)
	{
		var candidate = new NormalizationCatalogCandidate(Guid.NewGuid(), Guid.NewGuid(), target,
			null, ["lexical"], []);

		bool actual = ReceiptSuggestionEvidence.IsUseful(new("Synthetic market", source, null), candidate);

		actual.Should().Be(expected);
	}
}
