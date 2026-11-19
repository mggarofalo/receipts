using Application.Commands.Ynab.Connection;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Moq;

namespace Application.Tests.Commands.Ynab;

public class YnabConnectionCommandHandlerTests
{
	private readonly Mock<IYnabConnectionService> _connectionService = new(MockBehavior.Strict);

	[Fact]
	public async Task StartYnabAuthorization_ReturnsTheAddressTheServiceIssuedForThatUser()
	{
		// Arrange
		using CancellationTokenSource cancellation = new();
		_connectionService.Setup(s => s.StartAuthorizationAsync("admin-1", cancellation.Token))
			.ReturnsAsync("https://app.ynab.com/oauth/authorize?state=state-1");
		StartYnabAuthorizationCommandHandler handler = new(_connectionService.Object);

		// Act
		string? result = await handler.Handle(new StartYnabAuthorizationCommand("admin-1"), cancellation.Token);

		// Assert
		result.Should().Be("https://app.ynab.com/oauth/authorize?state=state-1");
		_connectionService.Verify(s => s.StartAuthorizationAsync("admin-1", cancellation.Token), Times.Once);
	}

	[Fact]
	public async Task StartYnabAuthorization_OAuthNotConfigured_ReturnsNull()
	{
		// Arrange
		_connectionService.Setup(s => s.StartAuthorizationAsync("admin-1", It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);
		StartYnabAuthorizationCommandHandler handler = new(_connectionService.Object);

		// Act
		string? result = await handler.Handle(new StartYnabAuthorizationCommand("admin-1"), CancellationToken.None);

		// Assert
		result.Should().BeNull();
	}

	[Theory]
	[InlineData(YnabAuthorizationCompletion.Connected)]
	[InlineData(YnabAuthorizationCompletion.AttemptNotValid)]
	[InlineData(YnabAuthorizationCompletion.RejectedByYnab)]
	[InlineData(YnabAuthorizationCompletion.YnabUnavailable)]
	public async Task CompleteYnabAuthorization_PassesUserCodeAndStateAndReturnsTheOutcome(YnabAuthorizationCompletion outcome)
	{
		// Arrange
		using CancellationTokenSource cancellation = new();
		_connectionService.Setup(s => s.CompleteAuthorizationAsync("admin-1", "code-1", "state-1", cancellation.Token))
			.ReturnsAsync(outcome);
		CompleteYnabAuthorizationCommandHandler handler = new(_connectionService.Object);

		// Act
		YnabAuthorizationCompletion result = await handler.Handle(
			new CompleteYnabAuthorizationCommand("admin-1", "code-1", "state-1"), cancellation.Token);

		// Assert
		result.Should().Be(outcome);
		_connectionService.Verify(
			s => s.CompleteAuthorizationAsync("admin-1", "code-1", "state-1", cancellation.Token), Times.Once);
	}

	[Theory]
	[InlineData(YnabRemoteRevocation.Succeeded)]
	[InlineData(YnabRemoteRevocation.Failed)]
	[InlineData(YnabRemoteRevocation.NotAttempted)]
	public async Task DisconnectYnab_ReturnsWhatHappenedToTheRemoteRevocation(YnabRemoteRevocation revocation)
	{
		// Arrange
		using CancellationTokenSource cancellation = new();
		_connectionService.Setup(s => s.DisconnectAsync(cancellation.Token)).ReturnsAsync(revocation);
		DisconnectYnabCommandHandler handler = new(_connectionService.Object);

		// Act
		YnabRemoteRevocation result = await handler.Handle(new DisconnectYnabCommand(), cancellation.Token);

		// Assert
		result.Should().Be(revocation);
		_connectionService.Verify(s => s.DisconnectAsync(cancellation.Token), Times.Once);
	}

	[Fact]
	public void CompleteYnabAuthorizationCommand_ToString_ContainsNeitherCodeNorState()
	{
		// Arrange — commands end up in logs and exception messages.
		CompleteYnabAuthorizationCommand command = new("admin-1", "auth-code-7c1d", "state-value-5e2a");

		// Act
		string text = command.ToString();

		// Assert
		text.Should().NotContain("auth-code-7c1d").And.NotContain("state-value-5e2a");
	}
}
