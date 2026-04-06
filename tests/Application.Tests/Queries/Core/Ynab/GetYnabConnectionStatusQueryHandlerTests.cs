using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetYnabConnectionStatusQueryHandlerTests
{
	private readonly Mock<IYnabApiClient> _ynabClientMock;
	private readonly Mock<IYnabSyncRecordService> _syncRecordServiceMock;
	private readonly GetYnabConnectionStatusQueryHandler _handler;

	public GetYnabConnectionStatusQueryHandlerTests()
	{
		_ynabClientMock = new Mock<IYnabApiClient>();
		_syncRecordServiceMock = new Mock<IYnabSyncRecordService>();
		_handler = new GetYnabConnectionStatusQueryHandler(_ynabClientMock.Object, _syncRecordServiceMock.Object);
	}

	[Fact]
	public async Task Handle_ReturnsNotConfigured_WhenPatMissing()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(false);

		// Act
		YnabConnectionStatus result = await _handler.Handle(new GetYnabConnectionStatusQuery(), CancellationToken.None);

		// Assert
		result.IsConfigured.Should().BeFalse();
		result.IsConnected.Should().BeFalse();
		result.LastSuccessfulSyncUtc.Should().BeNull();
		_ynabClientMock.Verify(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_ReturnsConnected_WhenBudgetsCallSucceeds()
	{
		// Arrange
		DateTimeOffset lastSync = DateTimeOffset.UtcNow.AddHours(-1);
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);
		_ynabClientMock.Setup(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync([new YnabBudget("b1", "Budget 1")]);
		_syncRecordServiceMock.Setup(s => s.GetLatestSuccessfulSyncTimestampAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(lastSync);

		// Act
		YnabConnectionStatus result = await _handler.Handle(new GetYnabConnectionStatusQuery(), CancellationToken.None);

		// Assert
		result.IsConfigured.Should().BeTrue();
		result.IsConnected.Should().BeTrue();
		result.LastSuccessfulSyncUtc.Should().Be(lastSync);
	}

	[Fact]
	public async Task Handle_ReturnsDisconnected_WhenBudgetsCallThrows()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);
		_ynabClientMock.Setup(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("YNAB auth failed"));
		_syncRecordServiceMock.Setup(s => s.GetLatestSuccessfulSyncTimestampAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((DateTimeOffset?)null);

		// Act
		YnabConnectionStatus result = await _handler.Handle(new GetYnabConnectionStatusQuery(), CancellationToken.None);

		// Assert
		result.IsConfigured.Should().BeTrue();
		result.IsConnected.Should().BeFalse();
		result.LastSuccessfulSyncUtc.Should().BeNull();
	}

	[Fact]
	public async Task Handle_ReturnsConnectedWithNullTimestamp_WhenNoSyncsExist()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);
		_ynabClientMock.Setup(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync([new YnabBudget("b1", "Budget 1")]);
		_syncRecordServiceMock.Setup(s => s.GetLatestSuccessfulSyncTimestampAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((DateTimeOffset?)null);

		// Act
		YnabConnectionStatus result = await _handler.Handle(new GetYnabConnectionStatusQuery(), CancellationToken.None);

		// Assert
		result.IsConfigured.Should().BeTrue();
		result.IsConnected.Should().BeTrue();
		result.LastSuccessfulSyncUtc.Should().BeNull();
	}
}
