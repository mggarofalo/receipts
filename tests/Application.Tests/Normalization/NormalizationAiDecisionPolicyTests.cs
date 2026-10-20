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
	[InlineData("revision")]
	[InlineData("description")]
	[InlineData("canonical-revision")]
	[InlineData("canonical-id")]
	[InlineData("score")]
	[InlineData("missing")]
	public void Review_RejectsChangedFactsEvenWhenSourceIsStillEligible(string change)
	{
		var expected = Source() with { CanonicalId = Guid.NewGuid(), CanonicalRevision = Guid.NewGuid(), CanonicalStatus = NormalizedDescriptionStatus.PendingReview, MatchScore = .8 };
		NormalizationAiSourceState? actual = change switch
		{
			"revision" => expected with { Revision = Guid.NewGuid() },
			"description" => expected with { Description = "Other milk" },
			"canonical-revision" => expected with { CanonicalRevision = Guid.NewGuid() },
			"canonical-id" => expected with { CanonicalId = Guid.NewGuid() },
			"score" => expected with { MatchScore = .9 },
			_ => null,
		};
		Action apply = () => NormalizationAiDecisionPolicy.RequireUnchangedSource(expected, actual);
		apply.Should().Throw<NormalizationAiException>().Which.Code.Should().Be("source_changed");
		NormalizationAiDecisionPolicy.RequireUnchangedSource(expected, expected);
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

	[Theory]
	[InlineData("abstain")]
	[InlineData("pending")]
	[InlineData("unknown")]
	public void Review_RejectsNonActionableSuggestions(string action)
	{
		Guid revision = Guid.NewGuid();
		Action apply = () => NormalizationAiDecisionPolicy.RequireReviewable("ready", revision, revision, [new("pending", action)]);
		apply.Should().Throw<NormalizationAiException>();
	}

	[Fact]
	public void NewName_CannotReactivateOrOverwriteAnExistingCanonical()
	{
		Action create = () => NormalizationAiDecisionPolicy.RequireValidTarget("create", "Milk", null, null, null, true);
		create.Should().Throw<NormalizationAiException>().Which.Code.Should().Be("target_changed");
		NormalizationAiDecisionPolicy.RequireValidTarget("create", "Milk", null, null, null, false);
	}

	[Fact]
	public void Reuse_RequiresSameActiveRevision()
	{
		Guid id = Guid.NewGuid(), revision = Guid.NewGuid();
		NormalizationAiDecisionPolicy.RequireValidTarget("reuse", null, id, revision, new(id, revision, NormalizedDescriptionStatus.Active), false);
		foreach (var target in new[] { new NormalizationAiCanonicalState(id, Guid.NewGuid(), NormalizedDescriptionStatus.Active), new(id, revision, NormalizedDescriptionStatus.Rejected), new(Guid.NewGuid(), revision, NormalizedDescriptionStatus.Active) })
		{
			Action apply = () => NormalizationAiDecisionPolicy.RequireValidTarget("reuse", null, id, revision, target, false);
			apply.Should().Throw<NormalizationAiException>().Which.Code.Should().Be("target_changed");
		}
	}
}
