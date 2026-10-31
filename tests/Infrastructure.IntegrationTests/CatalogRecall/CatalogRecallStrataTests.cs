using Application.Normalization;
using FluentAssertions;

namespace Infrastructure.IntegrationTests.CatalogRecall;

public sealed class CatalogRecallStrataTests
{
	[Theory]
	[InlineData("WHOLE MILK 1 GAL", "Whole milk", true)]
	[InlineData("Organic whole milk", "Milk", true)]
	[InlineData("PAPER TOWELS 6PK", "Paper towels", true)]
	[InlineData("KS ORG CHKN BRST", "Chicken breast", false)]
	[InlineData("SHRD MOZZ CHEESE", "Mozzarella cheese", false)]
	[InlineData("Ham", "Hamburger buns", false)]
	public void IsReadable_RequiresEveryProductWordAsAWholeWordOfTheTarget(string description, string target, bool readable)
		=> CatalogRecallStrata.IsReadable(description, target).Should().Be(readable);

	[Theory]
	[InlineData("TP 12")]
	[InlineData("12 OZ")]
	public void IsReadable_TreatsADescriptionWithNoProductWordAsAbbreviated(string description)
		=> CatalogRecallStrata.IsReadable(description, "Toilet paper 12 oz").Should().BeFalse();

	[Fact]
	public void IsExactName_FollowsTheExactChannelIncludingDisplayLabelsAndCase()
	{
		CatalogRecallStrata.IsExactName("BANANAS", "Bananas", null).Should().BeTrue();
		CatalogRecallStrata.IsExactName("OJ no pulp", "Orange juice", "oj NO PULP").Should().BeTrue();
		CatalogRecallStrata.IsExactName("Bananas 3 lb", "Bananas", null).Should().BeFalse();
	}

	[Fact]
	public void HasCrowdedToken_CountsNamesTheLexicalChannelWouldMatch()
	{
		var catalog = Enumerable.Range(0, CatalogRecallStrata.CrowdedTokenThreshold)
			.Select(index => ($"Variety {index} Cheese", (string?)null)).ToArray();

		CatalogRecallStrata.HasCrowdedToken("SHRD MOZZ CHEESE", catalog).Should().BeTrue();
		CatalogRecallStrata.HasCrowdedToken("SHRD MOZZ CHEESE", catalog[1..]).Should().BeFalse();
	}

	[Fact]
	public void Classify_AssignsOneSideOfEachPairAndAddsCrowdingOnlyWhenPresent()
	{
		CatalogRecallCase recallCase = new(new NormalizationSourceKey("Store", "Bananas", null), Guid.NewGuid(), "Bananas", null, null);

		CatalogRecallStrata.Classify(recallCase, sharesDescription: false, crowded: false).Should().BeEquivalentTo(
			[CatalogRecallStrata.Readable, CatalogRecallStrata.NovelDescription, CatalogRecallStrata.ExactName]);
		CatalogRecallStrata.Classify(recallCase with { Source = recallCase.Source with { Description = "BNNA" } },
			sharesDescription: true, crowded: true).Should().BeEquivalentTo(
			[CatalogRecallStrata.Abbreviated, CatalogRecallStrata.SameDescriptionOtherStore, CatalogRecallStrata.NotExactName,
				CatalogRecallStrata.CrowdedToken]);
	}
}
