using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Queries.Core.Ynab;

public class GetYnabConnectionStatusQueryHandlerTests
{
	private readonly Mock<IYnabApiClient> _ynabClientMock;
	private readonly Mock<IYnabCredentialSource> _credentialSourceMock;
	private readonly Mock<IYnabSyncRecordService> _syncRecordServiceMock;
	private readonly Mock<IYnabBudgetSelectionService> _budgetSelectionMock;
	private readonly GetYnabConnectionStatusQueryHandler _handler;

	public GetYnabConnectionStatusQueryHandlerTests()
	{
		_ynabClientMock = new Mock<IYnabApiClient>();
		_credentialSourceMock = new Mock<IYnabCredentialSource>();
		_syncRecordServiceMock = new Mock<IYnabSyncRecordService>();
		_budgetSelectionMock = new Mock<IYnabBudgetSelectionService>();
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync("budget-B");
		_handler = new GetYnabConnectionStatusQueryHandler(
			_ynabClientMock.Object,
			_credentialSourceMock.Object,
			_syncRecordServiceMock.Object,
			_budgetSelectionMock.Object,
			Mock.Of<Application.Interfaces.Services.IYnabSyncEventService>(),
			Mock.Of<Application.Interfaces.Services.IYnabResponseContext>(),
			Mock.Of<Microsoft.Extensions.Logging.ILogger<GetYnabConnectionStatusQueryHandler>>());
	}

	private void SetupCredential(YnabCredentialKind kind) =>
		_credentialSourceMock.Setup(s => s.GetStateAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabCredentialState(kind));

	[Fact]
	public async Task Handle_ReturnsNotConfigured_WhenPatMissing()
	{
		// Arrange
		SetupCredential(YnabCredentialKind.None);

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
		SetupCredential(YnabCredentialKind.PersonalAccessToken);
		_ynabClientMock.Setup(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync([new YnabBudget("b1", "Budget 1")]);
		_syncRecordServiceMock.Setup(s => s.GetLatestSuccessfulSyncTimestampAsync("budget-B", It.IsAny<CancellationToken>()))
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
		SetupCredential(YnabCredentialKind.PersonalAccessToken);
		_ynabClientMock.Setup(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("YNAB auth failed"));
		_syncRecordServiceMock.Setup(s => s.GetLatestSuccessfulSyncTimestampAsync("budget-B", It.IsAny<CancellationToken>()))
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
		SetupCredential(YnabCredentialKind.PersonalAccessToken);
		_ynabClientMock.Setup(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync([new YnabBudget("b1", "Budget 1")]);
		_syncRecordServiceMock.Setup(s => s.GetLatestSuccessfulSyncTimestampAsync("budget-B", It.IsAny<CancellationToken>()))
			.ReturnsAsync((DateTimeOffset?)null);

		// Act
		YnabConnectionStatus result = await _handler.Handle(new GetYnabConnectionStatusQuery(), CancellationToken.None);

		// Assert
		result.IsConfigured.Should().BeTrue();
		result.IsConnected.Should().BeTrue();
		result.LastSuccessfulSyncUtc.Should().BeNull();
	}

	[Fact]
	public async Task Handle_NoSelectedBudget_DoesNotProjectAnotherBudgetsLastSync()
	{
		SetupCredential(YnabCredentialKind.PersonalAccessToken);
		_ynabClientMock.Setup(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync([new YnabBudget("b1", "Budget 1")]);
		_budgetSelectionMock.Setup(s => s.GetSelectedBudgetIdAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		YnabConnectionStatus result = await _handler.Handle(
			new GetYnabConnectionStatusQuery(), CancellationToken.None);

		result.LastSuccessfulSyncUtc.Should().BeNull();
		_syncRecordServiceMock.Verify(s => s.GetLatestSuccessfulSyncTimestampAsync(
			It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	private void SetupCredentialStates(params YnabCredentialState[] states)
	{
		Moq.Language.ISetupSequentialResult<ValueTask<YnabCredentialState>> sequence =
			_credentialSourceMock.SetupSequence(s => s.GetStateAsync(It.IsAny<CancellationToken>()));
		foreach (YnabCredentialState state in states)
		{
			sequence = sequence.ReturnsAsync(state);
		}
	}

	private void SetupBudgetsSucceed() =>
		_ynabClientMock.Setup(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync([new YnabBudget("b1", "Budget 1")]);

	[Fact]
	public async Task Handle_OAuthConnectionWorking_ReportsOAuthConfiguredConnectedAndWhenItWasMade()
	{
		// Arrange
		DateTimeOffset connectedAt = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
		YnabCredentialState oauth = new(YnabCredentialKind.OAuth, OAuthAvailable: true, OAuthConnectedAtUtc: connectedAt);
		SetupCredentialStates(oauth, oauth);
		SetupBudgetsSucceed();

		// Act
		YnabConnectionStatus result = await _handler.Handle(new GetYnabConnectionStatusQuery(), CancellationToken.None);

		// Assert
		result.IsConfigured.Should().BeTrue();
		result.IsConnected.Should().BeTrue();
		result.Credential.Kind.Should().Be(YnabCredentialKind.OAuth);
		result.Credential.OAuthAvailable.Should().BeTrue();
		result.Credential.OAuthConnectedAtUtc.Should().Be(connectedAt);
	}

	[Fact]
	public async Task Handle_PersonalAccessTokenOnly_ReportsThePatAsTheCredential()
	{
		// Arrange
		YnabCredentialState pat = new(YnabCredentialKind.PersonalAccessToken);
		SetupCredentialStates(pat, pat);
		SetupBudgetsSucceed();

		// Act
		YnabConnectionStatus result = await _handler.Handle(new GetYnabConnectionStatusQuery(), CancellationToken.None);

		// Assert
		result.IsConfigured.Should().BeTrue();
		result.IsConnected.Should().BeTrue();
		result.Credential.Kind.Should().Be(YnabCredentialKind.PersonalAccessToken);
		result.Credential.OAuthAvailable.Should().BeFalse();
		result.Credential.OAuthConnectedAtUtc.Should().BeNull();
	}

	[Fact]
	public async Task Handle_OAuthAvailableButNothingConfigured_ReportsNoCredentialAndOAuthAvailable()
	{
		// Arrange
		SetupCredentialStates(new YnabCredentialState(YnabCredentialKind.None, OAuthAvailable: true));

		// Act
		YnabConnectionStatus result = await _handler.Handle(new GetYnabConnectionStatusQuery(), CancellationToken.None);

		// Assert
		result.IsConfigured.Should().BeFalse();
		result.IsConnected.Should().BeFalse();
		result.Credential.Kind.Should().Be(YnabCredentialKind.None);
		result.Credential.OAuthAvailable.Should().BeTrue();
		_ynabClientMock.Verify(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_ReconnectRequired_ReportsItWithoutCallingYnab()
	{
		// Arrange
		SetupCredentialStates(new YnabCredentialState(YnabCredentialKind.OAuthReconnectRequired, OAuthAvailable: true));

		// Act
		YnabConnectionStatus result = await _handler.Handle(new GetYnabConnectionStatusQuery(), CancellationToken.None);

		// Assert
		result.IsConfigured.Should().BeFalse();
		result.IsConnected.Should().BeFalse();
		result.Credential.Kind.Should().Be(YnabCredentialKind.OAuthReconnectRequired);
		result.Credential.OAuthAvailable.Should().BeTrue();
		_ynabClientMock.Verify(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_LiveCheckEndsTheConnection_ReportsReconnectRequiredNotTheStateItStartedWith()
	{
		// Arrange — usable when the check starts; YNAB refuses the refresh token during it.
		SetupCredentialStates(
			new YnabCredentialState(YnabCredentialKind.OAuth, OAuthAvailable: true, OAuthConnectedAtUtc: DateTimeOffset.UtcNow),
			new YnabCredentialState(YnabCredentialKind.OAuthReconnectRequired, OAuthAvailable: true));
		_ynabClientMock.Setup(c => c.GetBudgetsAsync(It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("YNAB no longer accepts the stored connection. Reconnect YNAB."));

		// Act
		YnabConnectionStatus result = await _handler.Handle(new GetYnabConnectionStatusQuery(), CancellationToken.None);

		// Assert
		result.IsConfigured.Should().BeFalse();
		result.IsConnected.Should().BeFalse();
		result.Credential.Kind.Should().Be(YnabCredentialKind.OAuthReconnectRequired);
		result.Credential.OAuthConnectedAtUtc.Should().BeNull();
	}

	[Fact]
	public async Task Handle_ConnectionEndsWhileBudgetsCameFromCache_IsNotReportedAsConnected()
	{
		// Arrange — the budget call succeeds (served from cache), yet by the end no credential is usable.
		SetupCredentialStates(
			new YnabCredentialState(YnabCredentialKind.OAuth, OAuthAvailable: true, OAuthConnectedAtUtc: DateTimeOffset.UtcNow),
			new YnabCredentialState(YnabCredentialKind.OAuthReconnectRequired, OAuthAvailable: true));
		SetupBudgetsSucceed();

		// Act
		YnabConnectionStatus result = await _handler.Handle(new GetYnabConnectionStatusQuery(), CancellationToken.None);

		// Assert
		result.IsConfigured.Should().BeFalse();
		result.IsConnected.Should().BeFalse();
		result.Credential.Kind.Should().Be(YnabCredentialKind.OAuthReconnectRequired);
	}
}
