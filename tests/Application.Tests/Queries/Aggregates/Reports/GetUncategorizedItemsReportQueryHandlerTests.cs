using Application.Classification;
using Application.Interfaces.Services;
using Application.Models.Reports;
using Application.Queries.Aggregates.Reports;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Aggregates.Reports;

public class GetUncategorizedItemsReportQueryHandlerTests
{
	private static readonly CategoryAssistanceSettings EnabledSettings = new(true, "jev", "model", 100, 3, Guid.NewGuid());
	private static readonly CategoryAssistanceCapability ReadyCapability = new("jev", CategoryAssistanceCapabilityState.Ready, ["model"]);

	private readonly Mock<IReportService> _reportServiceMock;
	private readonly Mock<ICategoryAssistanceSettingsStore> _settingsStoreMock;
	private readonly Mock<ICategoryAssistanceCapabilitySource> _capabilitySourceMock;
	private readonly GetUncategorizedItemsReportQueryHandler _handler;

	public GetUncategorizedItemsReportQueryHandlerTests()
	{
		_reportServiceMock = new Mock<IReportService>();
		_settingsStoreMock = new Mock<ICategoryAssistanceSettingsStore>();
		_capabilitySourceMock = new Mock<ICategoryAssistanceCapabilitySource>();
		_settingsStoreMock.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(EnabledSettings);
		_capabilitySourceMock.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ReadyCapability);
		_handler = new GetUncategorizedItemsReportQueryHandler(_reportServiceMock.Object,
			_settingsStoreMock.Object, _capabilitySourceMock.Object);
	}

	[Fact]
	public async Task Handle_DelegatesToReportService()
	{
		// Arrange
		GetUncategorizedItemsReportQuery query = new("description", "asc", 1, 50);
		UncategorizedItemsResult expectedResult = new([], 0);

		_reportServiceMock.Setup(s => s.GetUncategorizedItemsAsync(
			"description", "asc", 1, 50, "any", true, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expectedResult);

		// Act
		UncategorizedItemsResult result = await _handler.Handle(query, CancellationToken.None);

		// Assert
		result.Should().BeEquivalentTo(expectedResult with { SuggestionsAvailable = true });
		_reportServiceMock.Verify(s => s.GetUncategorizedItemsAsync(
			"description", "asc", 1, 50, "any", true, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_PassesAllParametersToService()
	{
		// Arrange
		GetUncategorizedItemsReportQuery query = new("total", "desc", 3, 25);
		UncategorizedItemsResult expectedResult = new([], 0);

		_reportServiceMock.Setup(s => s.GetUncategorizedItemsAsync(
			"total", "desc", 3, 25, "any", true, It.IsAny<CancellationToken>()))
			.ReturnsAsync(expectedResult);

		// Act
		await _handler.Handle(query, CancellationToken.None);

		// Assert
		_reportServiceMock.Verify(s => s.GetUncategorizedItemsAsync(
			"total", "desc", 3, 25, "any", true, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_ReturnsServiceResult()
	{
		// Arrange
		GetUncategorizedItemsReportQuery query = new("description", "asc", 1, 50);
		UncategorizedItemsResult expectedResult = new(
		[
			new UncategorizedItemRecord(Guid.NewGuid(), Guid.NewGuid(), "ABC",
				"Test Item", 1m, 5.00m, 5.00m),
		], 1);

		_reportServiceMock.Setup(s => s.GetUncategorizedItemsAsync(
			It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(),
			It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(expectedResult);

		// Act
		UncategorizedItemsResult result = await _handler.Handle(query, CancellationToken.None);

		// Assert
		result.TotalCount.Should().Be(1);
		result.Items.Should().ContainSingle();
	}

	[Fact]
	public async Task Handle_PassesSuggestionFilterToService()
	{
		// Arrange
		GetUncategorizedItemsReportQuery query = new("description", "asc", 1, 50, "with");

		_reportServiceMock.Setup(s => s.GetUncategorizedItemsAsync(
			"description", "asc", 1, 50, "with", true, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new UncategorizedItemsResult([], 0));

		// Act
		UncategorizedItemsResult result = await _handler.Handle(query, CancellationToken.None);

		// Assert
		result.SuggestionsAvailable.Should().BeTrue();
		_reportServiceMock.Verify(s => s.GetUncategorizedItemsAsync(
			"description", "asc", 1, 50, "with", true, It.IsAny<CancellationToken>()), Times.Once);
	}

	public static TheoryData<CategoryAssistanceSettings, CategoryAssistanceCapability> CannotSuggest => new()
	{
		{ EnabledSettings with { Enabled = false }, ReadyCapability },
		{ EnabledSettings, ReadyCapability with { State = CategoryAssistanceCapabilityState.Unavailable } },
		{ EnabledSettings, ReadyCapability with { Provider = "other" } },
		{ EnabledSettings, ReadyCapability with { Models = ["other-model"] } },
	};

	[Theory]
	[MemberData(nameof(CannotSuggest))]
	public async Task Handle_WithholdsSuggestions_WhenAssistanceCannotSuggest(
		CategoryAssistanceSettings settings, CategoryAssistanceCapability capability)
	{
		// Arrange
		_settingsStoreMock.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
		_capabilitySourceMock.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(capability);
		GetUncategorizedItemsReportQuery query = new("description", "asc", 1, 50, "with");

		_reportServiceMock.Setup(s => s.GetUncategorizedItemsAsync(
			"description", "asc", 1, 50, "with", false, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new UncategorizedItemsResult([], 0) { SuggestionsAvailable = true });

		// Act
		UncategorizedItemsResult result = await _handler.Handle(query, CancellationToken.None);

		// Assert
		result.SuggestionsAvailable.Should().BeFalse();
		_reportServiceMock.Verify(s => s.GetUncategorizedItemsAsync(
			"description", "asc", 1, 50, "with", false, It.IsAny<CancellationToken>()), Times.Once);
	}
}
