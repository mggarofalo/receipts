using API.Controllers;
using Application.Classification;
using FluentAssertions;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed class CategoryAssistanceReadApiTests
{
	[Theory]
	[InlineData(false, CategoryAssistanceCapabilityState.Ready)]
	[InlineData(true, CategoryAssistanceCapabilityState.Unavailable)]
	[InlineData(true, CategoryAssistanceCapabilityState.Unknown)]
	public async Task SuggestionsAreHiddenUnlessConsentAndCapabilityAreReady(bool enabled,
		CategoryAssistanceCapabilityState state)
	{
		var settings = new Mock<ICategoryAssistanceSettingsStore>();
		settings.Setup(x => x.GetAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CategoryAssistanceSettings(enabled, "jev", CategorySuggestionPolicy.Model,
				10, 3, Guid.NewGuid()));
		var capability = new Mock<ICategoryAssistanceCapabilitySource>();
		capability.Setup(x => x.GetAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CategoryAssistanceCapability("jev", state, [CategorySuggestionPolicy.Model]));
		var read = new Mock<ICategoryAssistanceReadStore>(MockBehavior.Strict);
		var controller = new CategoryAssistanceController(settings.Object, read.Object, capability.Object,
			Mock.Of<ICategoryAssistanceDecisionStore>());

		var response = await controller.Suggestions(ct: CancellationToken.None);

		response.Result.Should().BeOfType<Ok<CategoryAssistanceSuggestionPage>>()
			.Which.Value!.Items.Should().BeEmpty();
		read.Verify(x => x.ListSuggestionsAsync(It.IsAny<int>(), It.IsAny<int>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task StatusReportsConfiguredCapabilitySeparatelyFromConsent()
	{
		var settings = new Mock<ICategoryAssistanceSettingsStore>();
		settings.Setup(x => x.GetAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CategoryAssistanceSettings(false, null, null, 10, 3, Guid.NewGuid()));
		var capability = new Mock<ICategoryAssistanceCapabilitySource>();
		capability.Setup(x => x.GetAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new CategoryAssistanceCapability("jev", CategoryAssistanceCapabilityState.Ready,
				[CategorySuggestionPolicy.Model]));
		var controller = new CategoryAssistanceController(settings.Object,
			Mock.Of<ICategoryAssistanceReadStore>(), capability.Object,
			Mock.Of<ICategoryAssistanceDecisionStore>());

		var status = (await controller.Status(CancellationToken.None)).Value!;

		status.Capability.Should().Be("ready");
		status.ConsentEnabled.Should().BeFalse();
		status.SuggestionsAvailable.Should().BeFalse();
	}
}
