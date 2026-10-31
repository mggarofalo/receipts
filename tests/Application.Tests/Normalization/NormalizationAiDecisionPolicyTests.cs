using Application.NormalizationAi;
using Domain.NormalizedDescriptions;
using FluentAssertions;

namespace Application.Tests.Normalization;

public class NormalizationAiDecisionPolicyTests
{
	private static NormalizationAiSourceState Source() => new(Guid.NewGuid(), "Raw milk", null, null, null, null);

	[Theory]
	[InlineData("unresolved", true)]
	[InlineData("pending", true)]
	[InlineData("active", false)]
	[InlineData("rejected", false)]
	[InlineData("deleted-item", false)]
	[InlineData("deleted-parent", false)]
	[InlineData("template", false)]
	[InlineData("tombstone", false)]
	[InlineData("blank", false)]
	public void Eligibility_PreservesCuratedAndProtectedSources(string situation, bool eligible)
	{
		var source = Source();
		source = situation switch
		{
			"pending" => source with { CanonicalId = Guid.NewGuid(), CanonicalStatus = NormalizedDescriptionStatus.PendingReview },
			"active" => source with { CanonicalId = Guid.NewGuid(), CanonicalStatus = NormalizedDescriptionStatus.Active },
			"rejected" => source with { CanonicalId = Guid.NewGuid(), CanonicalStatus = NormalizedDescriptionStatus.Rejected },
			"deleted-item" => source with { ItemActive = false },
			"deleted-parent" => source with { ParentActive = false },
			"template" => source with { TemplateProtected = true },
			"tombstone" => source with { ExactRejected = true },
			"blank" => source with { Description = " \t " },
			_ => source,
		};
		NormalizationAiDecisionPolicy.IsEligibleSource(source).Should().Be(eligible);
	}

	[Theory]
	[InlineData("queued", false)]
	[InlineData("running", false)]
	[InlineData("ready", false)]
	[InlineData("applied", false)]
	[InlineData("cancelled", false)]
	[InlineData("failed", true)]
	[InlineData("unknown", true)]
	public void Retry_RequiresAnExplicitRecoverableTerminalState(string state, bool allowed)
	{
		Action retry = () => NormalizationAiDecisionPolicy.RequireRetryable(state);
		if (allowed)
		{
			retry.Should().NotThrow();
		}
		else
		{
			retry.Should().Throw<NormalizationAiException>().Which.Code.Should().Be("run_conflict");
		}
	}
}
