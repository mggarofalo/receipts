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

	[Theory]
	[InlineData(0.93, true)]
	[InlineData(ReceiptSuggestionEvidence.SemanticFloor, true)]
	[InlineData(0.79, false)]
	public void IsUseful_AdmitsASemanticNeighbourAtOrAboveTheFloor(double similarity, bool expected)
	{
		var candidate = new NormalizationCatalogCandidate(Guid.NewGuid(), Guid.NewGuid(), "Chicken breast",
			null, ["semantic"], [], similarity);

		bool actual = ReceiptSuggestionEvidence.IsUseful(new("Synthetic market", "KS ORG CHKN BRST", null), candidate);

		actual.Should().Be(expected);
	}

	[Fact]
	public void IsUseful_RejectsASemanticOnlyCandidateWithoutSimilarity()
	{
		var candidate = new NormalizationCatalogCandidate(Guid.NewGuid(), Guid.NewGuid(), "Chicken breast",
			null, ["semantic"], []);

		bool actual = ReceiptSuggestionEvidence.IsUseful(new("Synthetic market", "KS ORG CHKN BRST", null), candidate);

		actual.Should().BeFalse();
	}

	[Fact]
	public void IsUseful_AdmitsSimilarityAboveTheFloorWhenLexicalOverlapFails()
	{
		var candidate = new NormalizationCatalogCandidate(Guid.NewGuid(), Guid.NewGuid(), "Acme mallet",
			null, ["lexical", "semantic"], [], 0.91);

		bool actual = ReceiptSuggestionEvidence.IsUseful(new("Synthetic market", "Acme beans", null), candidate);

		actual.Should().BeTrue("similarity at or above the floor is evidence on its own");
	}
}
