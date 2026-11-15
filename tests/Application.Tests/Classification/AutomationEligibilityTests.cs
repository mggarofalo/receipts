using Application.Classification;
using FluentAssertions;

namespace Application.Tests.Classification;

public sealed class AutomationEligibilityTests
{
	[Theory]
	[InlineData("legacy_import", null)]
	[InlineData("legacy_write", "unresolved_path")]
	public void UntypedObservationIsEligible(string source, string? resolution) =>
		AutomationEligibility.IsUntyped(source, null, resolution).Should().BeTrue();

	[Theory]
	[InlineData("manual", null)]
	[InlineData("legacy_write", null)]
	[InlineData("legacy_write", "resolved_path")]
	[InlineData("approved_mapping", null)]
	[InlineData("automation", null)]
	[InlineData("unknown", "unresolved_path")]
	[InlineData(null, null)]
	public void ManualClearAndUnknownProvenanceAreProtected(string? source, string? resolution) =>
		AutomationEligibility.IsUntyped(source, null, resolution).Should().BeFalse();

	[Theory]
	[InlineData("legacy_import")]
	[InlineData("legacy_write")]
	[InlineData("manual")]
	public void AnyCategoryIsProtected(string source) =>
		AutomationEligibility.IsUntyped(source, Guid.NewGuid(), "unresolved_path").Should().BeFalse();
}
