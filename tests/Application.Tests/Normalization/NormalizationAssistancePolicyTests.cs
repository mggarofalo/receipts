using Application.Normalization;
using FluentAssertions;

namespace Application.Tests.Normalization;

public sealed class NormalizationAssistancePolicyTests
{
	[Theory]
	[InlineData(1, 30)]
	[InlineData(2, 60)]
	[InlineData(5, 480)]
	[InlineData(10, 1800)]
	public void AdmissionBackoff_IsBoundedWithoutImmediateRetryStorms(int attempts, int seconds)
		=> NormalizationAssistancePolicy.AdmissionBackoff(attempts).Should().Be(TimeSpan.FromSeconds(seconds));

	[Theory]
	[InlineData(true, null, 1)]
	[InlineData(true, "   ", 1)]
	[InlineData(true, "available-model", 0)]
	[InlineData(true, "available-model", 101)]
	[InlineData(false, null, 0)]
	public void Preferences_RejectInvalidDispatchOrBudgetChoices(bool enabled, string? model, int dailyLimit)
	{
		Action act = () => NormalizationAssistancePolicy.ValidatePreferences(new(enabled, model, dailyLimit, Guid.NewGuid()));
		var error = act.Should().Throw<NormalizationCatalogException>().Which;
		error.StatusCode.Should().Be(400);
		error.Code.Should().Be("invalid_assistance_preferences");
	}

	[Theory]
	[InlineData(false, null, 1)]
	[InlineData(true, "available-model", 1)]
	[InlineData(true, "available-model", 100)]
	public void Preferences_AllowOptOutWithoutAProviderAndInclusiveBudgetBounds(bool enabled, string? model, int dailyLimit)
	{
		Action act = () => NormalizationAssistancePolicy.ValidatePreferences(new(enabled, model, dailyLimit, Guid.NewGuid()));
		act.Should().NotThrow();
	}

	[Fact]
	public void Preferences_RejectOversizedProviderModelIdentifiers()
	{
		Action act = () => NormalizationAssistancePolicy.ValidatePreferences(new(true, new string('x', 201), 1, Guid.NewGuid()));
		act.Should().Throw<NormalizationCatalogException>().Which.StatusCode.Should().Be(400);
	}
}
