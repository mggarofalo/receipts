using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;

namespace Application.Tests.Normalization;

public sealed class ReceiptNormalizationPolicyTests
{
	[Fact]
	public void VerificationEvidence_CannotSurviveChangedSourceAssignmentIdentityOrRejectedTarget()
	{
		var reviewed = new ReceiptVerificationFacts(true, true, true, true, true, false, NormalizationReviewState.Verified);
		ReceiptNormalizationPolicy.EvidenceIsCurrent(reviewed).Should().BeTrue();
		ReceiptNormalizationPolicy.EvidenceIsCurrent(reviewed with { SourceCurrent = false }).Should().BeFalse();
		ReceiptNormalizationPolicy.EvidenceIsCurrent(reviewed with { AssignmentCurrent = false }).Should().BeFalse();
		ReceiptNormalizationPolicy.EvidenceIsCurrent(reviewed with { IdentityCurrent = false }).Should().BeFalse();
		ReceiptNormalizationPolicy.EvidenceIsCurrent(reviewed with { TargetActive = false }).Should().BeFalse();
		ReceiptNormalizationPolicy.EvidenceIsCurrent(reviewed with { State = NormalizationReviewState.NeedsReview }).Should().BeFalse();
	}

	[Fact]
	public void VerificationEvidence_IndependentReviewSurvivesRevocationButMappingDependentReviewDoesNot()
	{
		var mapped = new ReceiptVerificationFacts(true, true, true, true, false, true, NormalizationReviewState.Verified);
		ReceiptNormalizationPolicy.EvidenceIsCurrent(mapped).Should().BeTrue();
		ReceiptNormalizationPolicy.EvidenceIsCurrent(mapped with { MappingCurrent = false }).Should().BeFalse();
		ReceiptNormalizationPolicy.EvidenceIsCurrent(mapped with { MappingCurrent = false, IndependentlyReviewed = true }).Should().BeTrue();
	}

	[Fact]
	public void VerificationEvidence_ExplicitExclusionDoesNotRequireAProductButStillRequiresCurrentSource()
	{
		var excluded = new ReceiptVerificationFacts(true, true, true, false, true, false, NormalizationReviewState.Excluded);
		ReceiptNormalizationPolicy.EvidenceIsCurrent(excluded).Should().BeTrue();
		ReceiptNormalizationPolicy.EvidenceIsCurrent(excluded with { SourceCurrent = false }).Should().BeFalse();
		ReceiptNormalizationPolicy.EvidenceIsCurrent(excluded with { IndependentlyReviewed = false }).Should().BeFalse();
	}

	[Theory]
	[InlineData(null, false, false, NormalizationReviewState.NeedsReview, "unknown_source")]
	[InlineData(ReceiptNormalizationIntentKind.KnownSource, false, false, NormalizationReviewState.NeedsReview, "unknown_source")]
	[InlineData(null, true, false, NormalizationReviewState.Verified, "approved_mapping")]
	[InlineData(null, true, true, NormalizationReviewState.Excluded, "approved_mapping")]
	[InlineData(ReceiptNormalizationIntentKind.Defer, true, false, NormalizationReviewState.NeedsReview, "deferred")]
	[InlineData(ReceiptNormalizationIntentKind.Exclude, true, false, NormalizationReviewState.Excluded, "purchase_exclusion")]
	[InlineData(ReceiptNormalizationIntentKind.ConfirmExisting, false, false, NormalizationReviewState.Verified, "purchase_review")]
	[InlineData(ReceiptNormalizationIntentKind.ConfirmNew, false, false, NormalizationReviewState.Verified, "purchase_review")]
	public void Decision_ExplicitPurchaseChoiceOverridesAutomaticReuseAndOmissionNeedsEvidence(
		ReceiptNormalizationIntentKind? intent, bool mapping, bool excluded,
		NormalizationReviewState expectedState, string expectedReason)
	{
		var result = ReceiptNormalizationPolicy.Decide(intent, mapping, excluded);
		result.State.Should().Be(expectedState);
		result.Reason.Should().Be(expectedReason);
	}

	[Fact]
	public void Validate_ChangedStoreDescriptionOrCodeRejectsAChoiceForThePreviousObservation()
	{
		var source = new NormalizationSourceKey("Store", "MILK 1L", "007");
		var intent = Existing(NormalizationCatalogPolicy.Hash(source));
		foreach (var changed in new[]
		{
			source with { Location = "Other Store" },
			source with { Description = "MILK 2L" },
			source with { ItemCode = null },
		})
		{
			Action act = () => ReceiptNormalizationPolicy.Validate(intent, NormalizationCatalogPolicy.Hash(changed));
			act.Should().Throw<NormalizationCatalogException>().Which.Code.Should().Be("normalization_source_changed");
		}
	}

	[Theory]
	[MemberData(nameof(MalformedChoices))]
	public void Validate_MalformedChoiceCannotAcquireAuthority(ReceiptNormalizationIntent intent)
	{
		Action act = () => ReceiptNormalizationPolicy.Validate(intent, "source");
		act.Should().Throw<NormalizationCatalogException>().Which.Code.Should().Be("invalid_normalization_intent");
	}

	public static IEnumerable<object[]> MalformedChoices()
	{
		var existing = Existing("source");
		yield return [new ReceiptNormalizationIntent(ReceiptNormalizationIntentKind.KnownSource, Guid.NewGuid(), "source")];
		yield return [existing with { ClientRowId = Guid.Empty }];
		yield return [existing with { Kind = (ReceiptNormalizationIntentKind)99 }];
		yield return [existing with { TargetId = null }];
		yield return [existing with { ExpectedTargetRevision = null }];
		yield return [existing with { TargetId = Guid.Empty }];
		yield return [existing with { ExpectedTargetRevision = Guid.Empty }];
		yield return [existing with { ExpectedMappingRevision = Guid.Empty }];
		yield return [existing with { ExpectedItemRevision = Guid.Empty }];
		yield return [existing with { NewName = "Unexpected new name" }];
		yield return [new ReceiptNormalizationIntent(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), "source", NewName: "  ")];
		yield return [new ReceiptNormalizationIntent(ReceiptNormalizationIntentKind.ConfirmNew, Guid.NewGuid(), "source", NewName: new string('x', 201))];
		yield return [new ReceiptNormalizationIntent(ReceiptNormalizationIntentKind.Exclude, Guid.NewGuid(), "source", Reason: " \t")];
		yield return [new ReceiptNormalizationIntent(ReceiptNormalizationIntentKind.Exclude, Guid.NewGuid(), "source", Reason: new string('x', 501))];
		yield return [new ReceiptNormalizationIntent(ReceiptNormalizationIntentKind.Defer, Guid.NewGuid(), "source", RememberSource: true)];
	}

	[Theory]
	[InlineData(ReceiptNormalizationIntentKind.KnownSource)]
	[InlineData(ReceiptNormalizationIntentKind.Defer)]
	[InlineData(ReceiptNormalizationIntentKind.Exclude)]
	[InlineData(ReceiptNormalizationIntentKind.ConfirmExisting)]
	[InlineData(ReceiptNormalizationIntentKind.ConfirmNew)]
	public void Validate_CompleteIntentShapesAreAccepted(ReceiptNormalizationIntentKind kind)
	{
		var intent = kind == ReceiptNormalizationIntentKind.ConfirmExisting ? Existing("source")
			: new(kind, Guid.NewGuid(), "source", NewName: kind == ReceiptNormalizationIntentKind.ConfirmNew ? "Milk 1L" : null,
				Reason: kind == ReceiptNormalizationIntentKind.Exclude ? "Not a product" : null,
				ExpectedMappingRevision: kind == ReceiptNormalizationIntentKind.KnownSource ? Guid.NewGuid() : null);
		Action act = () => ReceiptNormalizationPolicy.Validate(intent, "source");
		act.Should().NotThrow();
	}

	private static ReceiptNormalizationIntent Existing(string fingerprint) => new(
		ReceiptNormalizationIntentKind.ConfirmExisting, Guid.NewGuid(), fingerprint, Guid.NewGuid(), Guid.NewGuid());
}
