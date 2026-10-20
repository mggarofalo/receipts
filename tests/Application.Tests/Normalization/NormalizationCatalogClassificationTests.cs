using Application.Normalization;
using FluentAssertions;
namespace Application.Tests.Normalization;

public sealed class NormalizationCatalogClassificationTests
{
	[Theory]
	[InlineData("unassigned", false, false, "unassigned", false, true)]
	[InlineData("automatic", false, false, "automatic", false, true)]
	[InlineData("legacy_unknown", false, false, "legacy_unknown", false, false)]
	[InlineData("legacy_unknown", true, false, "legacy_unknown", false, true)]
	[InlineData("manual", true, true, "protected", true, false)]
	[InlineData("template", true, true, "protected", true, false)]
	[InlineData("reviewed", true, true, "reviewed", true, false)]
	[InlineData("mapped", true, false, "mapped", true, false)]
	[InlineData("mapped", true, true, "revisable", false, true)]
	[InlineData("excluded", true, false, "excluded", true, false)]
	[InlineData("excluded", true, true, "revisable", false, true)]
	[InlineData("future_unknown_origin", true, true, "unrecognized", true, false)]
	public void Classification_ConservativelyOwnsConsentProtectionAndCorrection(string origin, bool legacy, bool revoked, string disposition, bool protectedValue, bool eligible)
	{
		var decision = NormalizationCatalogPolicy.Classify(new(origin, true, true, false, false, false, revoked), legacy);
		decision.Should().Be(new CatalogAssignmentDecision(disposition, protectedValue, eligible));
	}
	[Theory]
	[InlineData(false, true, false, false, false, "unavailable")]
	[InlineData(true, false, false, false, false, "unavailable")]
	[InlineData(true, true, true, false, false, "rejected")]
	[InlineData(true, true, false, true, false, "rejected")]
	[InlineData(true, true, false, false, true, "protected")]
	public void RejectionDeletionAndTemplateFactsOverrideOtherwiseEligibleOrigin(bool itemLive, bool parentLive, bool targetRejected, bool sourceRejected, bool template, string disposition)
		=> NormalizationCatalogPolicy.Classify(new("automatic", itemLive, parentLive, targetRejected, sourceRejected, template, true), true)
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
