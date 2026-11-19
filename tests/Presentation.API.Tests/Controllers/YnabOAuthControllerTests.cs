using System.Reflection;
using System.Security.Claims;
using API.Controllers;
using API.Generated.Dtos;
using API.Http;
using API.Mapping.Core;
using Application.Commands.Ynab.Connection;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Moq;
using DtoRemoteRevocation = API.Generated.Dtos.YnabRemoteRevocation;
using RemoteRevocation = Application.Models.Ynab.YnabRemoteRevocation;

namespace Presentation.API.Tests.Controllers;

public class YnabOAuthControllerTests
{
	private const string AdminId = "admin-1";
	private const string Code = "auth-code-7c1d";
	private const string State = "state-value-5e2a";

	private readonly Mock<IMediator> _mediator = new(MockBehavior.Strict);
	private readonly YnabOAuthController _controller;

	public YnabOAuthControllerTests()
	{
		_controller = CreateController(new Claim(ClaimTypes.NameIdentifier, AdminId));
	}

	private YnabOAuthController CreateController(params Claim[] claims) =>
		new(_mediator.Object, new YnabMapper())
		{
			ControllerContext = new ControllerContext
			{
				HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
			},
		};

	private static CompleteYnabAuthorizationRequest Request() => new() { Code = Code, State = State };

	private void SetupCompletion(YnabAuthorizationCompletion completion) =>
		_mediator
			.Setup(m => m.Send(new CompleteYnabAuthorizationCommand(AdminId, Code, State), It.IsAny<CancellationToken>()))
			.ReturnsAsync(completion);

	#region Authorization

	// The class-level RequireAdmin policy is asserted in AdminAuthorizationAttributeTests and, over
	// HTTP, in YnabOAuthHttpTests. This guards the one way an action could step out from under it.
	[Theory]
	[InlineData(nameof(YnabOAuthController.StartAuthorization))]
	[InlineData(nameof(YnabOAuthController.CompleteAuthorization))]
	[InlineData(nameof(YnabOAuthController.Disconnect))]
	public void Action_DoesNotOptOutOfAuthorization(string action)
	{
		// Assert
		typeof(YnabOAuthController).GetMethod(action, BindingFlags.Public | BindingFlags.Instance)!
			.GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty();
	}

	#endregion

	#region Start

	[Fact]
	public async Task StartAuthorization_OAuthConfigured_Returns200WithTheAuthorizationAddressForTheCaller()
	{
		// Arrange
		const string url = "https://app.ynab.com/oauth/authorize?client_id=client-id-1&state=state-value-5e2a&code_challenge_method=S256";
		_mediator.Setup(m => m.Send(new StartYnabAuthorizationCommand(AdminId), It.IsAny<CancellationToken>()))
			.ReturnsAsync(url);

		// Act
		var result = await _controller.StartAuthorization(CancellationToken.None);

		// Assert
		Ok<YnabAuthorizationStartResponse> ok = result.Result.Should().BeOfType<Ok<YnabAuthorizationStartResponse>>().Subject;
		ok.Value!.AuthorizationUrl.Should().Be(new Uri(url));
		ok.Value.AuthorizationUrl.OriginalString.Should().Be(url);
	}

	[Fact]
	public async Task StartAuthorization_OAuthNotConfigured_Returns409ProblemSayingSo()
	{
		// Arrange
		_mediator.Setup(m => m.Send(new StartYnabAuthorizationCommand(AdminId), It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		// Act
		var result = await _controller.StartAuthorization(CancellationToken.None);

		// Assert
		ConflictProblem problem = result.Result.Should().BeOfType<ConflictProblem>().Subject;
		problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
		problem.Value.Status.Should().Be(StatusCodes.Status409Conflict);
		problem.Value.Detail.Should().Be("YNAB OAuth is not configured on this deployment");
	}

	[Fact]
	public async Task StartAuthorization_CallerHasNoUserId_IsForbiddenAndCreatesNoAttempt()
	{
		// Arrange — an attempt must be bound to someone.
		YnabOAuthController controller = CreateController(new Claim(ClaimTypes.Role, "Admin"));

		// Act
		var result = await controller.StartAuthorization(CancellationToken.None);

		// Assert
		result.Result.Should().BeOfType<ForbidHttpResult>();
		_mediator.Verify(m => m.Send(It.IsAny<StartYnabAuthorizationCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	#endregion

	#region Callback

	[Fact]
	public async Task CompleteAuthorization_Connected_Returns200WithTheConnectionStatus()
	{
		// Arrange
		DateTimeOffset connectedAt = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
		SetupCompletion(YnabAuthorizationCompletion.Connected);
		_mediator.Setup(m => m.Send(It.IsAny<GetYnabConnectionStatusQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabConnectionStatus(
				true, true, null, new YnabCredentialState(YnabCredentialKind.OAuth, OAuthAvailable: true, OAuthConnectedAtUtc: connectedAt)));

		// Act
		var result = await _controller.CompleteAuthorization(Request(), CancellationToken.None);

		// Assert
		YnabConnectionStatusResponse response = result.Result.Should().BeOfType<Ok<YnabConnectionStatusResponse>>().Subject.Value!;
		response.CredentialSource.Should().Be(YnabCredentialSource.Oauth);
		response.IsConfigured.Should().BeTrue();
		response.IsConnected.Should().BeTrue();
		response.ReconnectRequired.Should().BeFalse();
		response.OauthAvailable.Should().BeTrue();
		response.OauthConnectedAtUtc.Should().Be(connectedAt);
	}

	[Fact]
	public async Task CompleteAuthorization_AttemptNotValid_Returns400ProblemSayingTheAttemptIsNotValid()
	{
		// Arrange
		SetupCompletion(YnabAuthorizationCompletion.AttemptNotValid);

		// Act
		var result = await _controller.CompleteAuthorization(Request(), CancellationToken.None);

		// Assert
		BadRequestProblem problem = result.Result.Should().BeOfType<BadRequestProblem>().Subject;
		problem.Value.Status.Should().Be(StatusCodes.Status400BadRequest);
		problem.Value.Detail.Should().Contain("authorization attempt is not valid");
		_mediator.Verify(m => m.Send(It.IsAny<GetYnabConnectionStatusQuery>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task CompleteAuthorization_RejectedByYnab_Returns400ProblemSayingYnabRejectedIt()
	{
		// Arrange
		SetupCompletion(YnabAuthorizationCompletion.RejectedByYnab);

		// Act
		var result = await _controller.CompleteAuthorization(Request(), CancellationToken.None);

		// Assert
		BadRequestProblem problem = result.Result.Should().BeOfType<BadRequestProblem>().Subject;
		problem.Value.Status.Should().Be(StatusCodes.Status400BadRequest);
		problem.Value.Detail.Should().Contain("YNAB rejected the authorization");
		problem.Value.Detail.Should().NotContain("not valid", "the two refusals must be told apart");
	}

	[Fact]
	public async Task CompleteAuthorization_YnabUnavailable_ReturnsBodiless503()
	{
		// Arrange
		SetupCompletion(YnabAuthorizationCompletion.YnabUnavailable);

		// Act
		var result = await _controller.CompleteAuthorization(Request(), CancellationToken.None);

		// Assert
		result.Result.Should().BeOfType<StatusCodeHttpResult>()
			.Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
		_mediator.Verify(m => m.Send(It.IsAny<GetYnabConnectionStatusQuery>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Theory]
	[InlineData(YnabAuthorizationCompletion.AttemptNotValid)]
	[InlineData(YnabAuthorizationCompletion.RejectedByYnab)]
	public async Task CompleteAuthorization_Refused_ProblemNeverEchoesTheCodeOrState(YnabAuthorizationCompletion completion)
	{
		// Arrange
		SetupCompletion(completion);

		// Act
		var result = await _controller.CompleteAuthorization(Request(), CancellationToken.None);

		// Assert
		string detail = result.Result.Should().BeOfType<BadRequestProblem>().Subject.Value.Detail!;
		detail.Should().NotContain(Code).And.NotContain(State);
	}

	[Fact]
	public async Task CompleteAuthorization_CallerHasNoUserId_IsForbiddenAndNothingIsCompleted()
	{
		// Arrange
		YnabOAuthController controller = CreateController(new Claim(ClaimTypes.Role, "Admin"));

		// Act
		var result = await controller.CompleteAuthorization(Request(), CancellationToken.None);

		// Assert
		result.Result.Should().BeOfType<ForbidHttpResult>();
		_mediator.Verify(m => m.Send(It.IsAny<CompleteYnabAuthorizationCommand>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	#endregion

	#region Disconnect

	[Theory]
	[InlineData(RemoteRevocation.Succeeded, DtoRemoteRevocation.Succeeded)]
	[InlineData(RemoteRevocation.Failed, DtoRemoteRevocation.Failed)]
	[InlineData(RemoteRevocation.NotAttempted, DtoRemoteRevocation.NotAttempted)]
	public async Task Disconnect_Returns200ReportingWhatHappenedToTheRemoteRevocation(
		RemoteRevocation revocation, DtoRemoteRevocation expected)
	{
		// Arrange
		_mediator.Setup(m => m.Send(It.IsAny<DisconnectYnabCommand>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(revocation);

		// Act
		Ok<YnabDisconnectResponse> result = await _controller.Disconnect(CancellationToken.None);

		// Assert
		result.StatusCode.Should().Be(StatusCodes.Status200OK);
		result.Value!.RemoteRevocation.Should().Be(expected);
		_mediator.Verify(m => m.Send(It.IsAny<DisconnectYnabCommand>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	#endregion
}
