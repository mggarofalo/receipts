using API.Controllers;
using API.Http;
using Application.Classification;
using FluentAssertions;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed class CategoryAssistanceReadApiTests
{
	[Fact]
	public async Task SingleSuggestionUsesConsentAndCapabilityVisibility()
	{
		var attemptId = Guid.NewGuid();
		var settings = new Mock<ICategoryAssistanceSettingsStore>();
		settings.Setup(x => x.GetAsync(It.IsAny<CancellationToken>()))
				.ReturnsAsync(new CategoryAssistanceSettings(false, "jev", CategorySuggestionPolicy.Model,
						10, 3, Guid.NewGuid()));
		var capability = new Mock<ICategoryAssistanceCapabilitySource>();
		capability.Setup(x => x.GetAsync(It.IsAny<CancellationToken>()))
				.ReturnsAsync(new CategoryAssistanceCapability("jev", CategoryAssistanceCapabilityState.Ready,
						[CategorySuggestionPolicy.Model]));
		var read = new Mock<ICategoryAssistanceReadStore>(MockBehavior.Strict);
		var controller = new CategoryAssistanceController(settings.Object, read.Object, capability.Object,
				Mock.Of<ICategoryAssistanceDecisionStore>());

		(await controller.Suggestion(attemptId, CancellationToken.None)).Result
				.Should().BeOfType<NotFoundProblem>();
		read.Verify(x => x.GetSuggestionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

		settings.Setup(x => x.GetAsync(It.IsAny<CancellationToken>()))
				.ReturnsAsync(new CategoryAssistanceSettings(true, "jev", CategorySuggestionPolicy.Model,
						10, 3, Guid.NewGuid()));
		read.Setup(x => x.GetSuggestionAsync(attemptId, It.IsAny<CancellationToken>()))
				.ReturnsAsync((CategoryAssistanceSuggestionRead?)null);
		(await controller.Suggestion(attemptId, CancellationToken.None)).Result
				.Should().BeOfType<NotFoundProblem>();
	}
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
