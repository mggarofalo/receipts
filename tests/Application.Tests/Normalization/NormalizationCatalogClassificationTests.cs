using Application.Normalization;
using FluentAssertions;
namespace Application.Tests.Normalization;

public sealed class NormalizationCatalogClassificationTests
{
	[Theory]
	[InlineData("unassigned", false, false, false, false, "unassigned", false, true)]
	[InlineData("automatic", false, false, false, false, "automatic", false, true)]
	[InlineData("manual", false, true, false, false, "legacy_unknown", false, false)]
	[InlineData("template", true, true, false, false, "legacy_unknown", false, true)]
	[InlineData("reviewed", true, true, false, false, "legacy_unknown", false, true)]
	[InlineData("future_unknown_origin", true, true, false, false, "legacy_unknown", false, true)]
	[InlineData("manual", true, true, true, false, "reviewed", true, false)]
	[InlineData("template", true, true, true, false, "reviewed", true, false)]
	[InlineData("reviewed", true, true, true, false, "reviewed", true, false)]
	[InlineData("mapped", true, true, true, false, "mapped", true, false)]
	[InlineData("mapped", true, true, false, true, "revisable", false, true)]
	[InlineData("excluded", true, false, true, false, "excluded", true, false)]
	[InlineData("excluded", true, false, false, true, "revisable", false, true)]
	public void Classification_SeparatesHistoricalProvenanceFromReviewedAuthority(string origin, bool legacy, bool linked, bool proof, bool revoked, string disposition, bool protectedValue, bool eligible)
	{
		var decision = NormalizationCatalogPolicy.Classify(new(origin, true, true, false, false, origin == "template", revoked, proof, linked), legacy);
		decision.Should().Be(new CatalogAssignmentDecision(disposition, protectedValue, eligible));
	}
	[Theory]
	[InlineData(false, true, false, false, "unavailable")]
	[InlineData(true, false, false, false, "unavailable")]
	[InlineData(true, true, true, false, "rejected")]
	[InlineData(true, true, false, true, "rejected")]
	public void RejectionAndDeletionOverrideOtherwiseReviewedEvidence(bool itemLive, bool parentLive, bool targetRejected, bool sourceRejected, string disposition)
		=> NormalizationCatalogPolicy.Classify(new("mapped", itemLive, parentLive, targetRejected, sourceRejected, false, true, true, true), true)
			.Should().Be(new CatalogAssignmentDecision(disposition, true, false));
	[Theory]
	[InlineData("approve")]
	[InlineData("resume")]
	[InlineData("cancel")]
	[InlineData("undo")]
	public void RestoredOperation_IsEvidenceOnlyAndCannotBeReplayed(string action)
	{
		Action replay = () => NormalizationCatalogPolicy.RequireAction("restored", action);
		replay.Should().Throw<NormalizationCatalogException>().Which.Code.Should().Be("operation_state");
	}
}
