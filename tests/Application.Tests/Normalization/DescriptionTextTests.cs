using Application.Normalization;
using FluentAssertions;

namespace Application.Tests.Normalization;

/// <summary>Characterises the shared description normaliser (RECEIPTS-1100); these values were measured from main.</summary>
public sealed class DescriptionTextTests
{
	public static TheoryData<string, string[], string[]> LexicalCases => new()
	{
		{ "Crème BRÛLÉE", ["crème", "brûlée"], ["crème", "brûlée"] },
		{ "half-and-half 1/2 GAL", ["half"], ["half", "and", "gal"] },
		{ "\tKS ORG CHKN BRST\t", ["org", "chkn", "brst"], ["ks", "org", "chkn", "brst"] },
		// Invariant lower-casing leaves U+0130 (capital I with dot above) as-is rather than mapping it to "i̇".
		{ "İSTANBUL ıi", ["İstanbul"], ["İstanbul", "ıi"] },
		// A supplementary letter is two surrogate UTF-16 units that \p{L} does not match, so Receipt mode is left with "BC", too short to keep.
		{ "\U0001D538BC def", ["def"], ["\U0001D538bc", "def"] },
		{ "PAPER TWLS 6PK/12", ["paper", "twls"], ["paper", "twls", "6pk", "12"] },
	};

	[Theory]
	[MemberData(nameof(LexicalCases))]
	public void LexicalTokens_MatchMeasuredValuesInOrder(string text, string[] receipt, string[] catalog)
	{
		// Act
		var receiptTokens = DescriptionText.LexicalTokens(text, DescriptionTokenMode.Receipt);
		var catalogTokens = DescriptionText.LexicalTokens(text, DescriptionTokenMode.Catalog);

		// Assert
		receiptTokens.Should().Equal(receipt);
		catalogTokens.Should().Equal(catalog);
	}

	[Theory]
	[InlineData(DescriptionTokenMode.Receipt)]
	[InlineData(DescriptionTokenMode.Catalog)]
	public void LexicalTokens_AreDistinctAndKeepFirstOccurrenceOrder(DescriptionTokenMode mode)
	{
		// Act
		var tokens = DescriptionText.LexicalTokens("Milk BREAD milk bread Eggs", mode);

		// Assert
		tokens.Should().Equal("milk", "bread", "eggs");
	}

	[Theory]
	[InlineData(DescriptionTokenMode.Receipt)]
	[InlineData(DescriptionTokenMode.Catalog)]
	public void LexicalTokens_StopAtMaximumTokens(DescriptionTokenMode mode)
	{
		// Arrange
		const string text = "apple banana cherry damson elder fig grape honeydew kiwi lemon";

		// Act
		var tokens = DescriptionText.LexicalTokens(text, mode);

		// Assert
		DescriptionText.MaximumTokens.Should().Be(8);
		tokens.Should().Equal("apple", "banana", "cherry", "damson", "elder", "fig", "grape", "honeydew");
	}

	[Fact]
	public void ProductWords_KeepCaseAndDropUnitsAndModifiers()
	{
		// Act
		var words = DescriptionText.ProductWords("Organic Milk 1 GAL");

		// Assert
		words.Should().Equal("Milk");
	}

	[Fact]
	public void ProductWords_MinimumLengthTwoKeepsShortWords()
	{
		// Act
		var words = DescriptionText.ProductWords("KS ORG CHKN", minimumLength: 2);

		// Assert
		words.Should().Equal("KS", "ORG", "CHKN");
	}

	[Fact]
	public void SameText_IgnoresAnyOuterWhitespaceAndCase()
	{
		// Unlike the source key's Trim(' '), this trims tabs and other whitespace too.
		DescriptionText.SameText("\tMilk ", "milk").Should().BeTrue();
	}

	[Fact]
	public void SameText_DistinguishesDifferentText()
	{
		DescriptionText.SameText("Milk", "Milk 2").Should().BeFalse();
	}

	[Fact]
	public void Lower_LowersNonAsciiLetters()
	{
		DescriptionText.Lower("ÉCLAIR").Should().Be("éclair");
	}
}
