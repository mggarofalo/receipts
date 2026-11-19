using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using API.Configuration;
using API.Controllers;
using API.Mapping.Core;
using Application.Commands.Ynab.Connection;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Presentation.API.Tests.Fixtures;

namespace Presentation.API.Tests.Configuration;

/// <summary>
/// The YNAB OAuth endpoints over HTTP: who may call them, and the wire shape of each answer.
/// The Mediator is mocked, so nothing here reaches YNAB or a database.
/// </summary>
public class YnabOAuthHttpTests
{
	private const string AuthorizationPath = "/api/ynab/oauth/authorization";
	private const string CallbackPath = "/api/ynab/oauth/callback";
	private const string ConnectionPath = "/api/ynab/oauth/connection";

	private readonly Mock<IMediator> _mediator = new();

	private async Task<WebApplication> StartAsync()
	{
		WebApplicationBuilder builder = ConfiguredApiTestHost.CreateBuilder([]);
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
		builder.Services.AddAuthorization(options =>
			options.AddPolicy("RequireAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("Admin")));
		// The production JSON, validation and problem-details setup, so the wire shape is the real one.
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration);
		builder.Services.AddControllers().AddApplicationPart(typeof(YnabOAuthController).Assembly);
		builder.Services.AddSingleton(_mediator.Object);
		builder.Services.AddSingleton<YnabMapper>();
		WebApplication app = builder.Build();
		app.UseAuthentication();
		app.UseAuthorization();
		app.MapControllers();
		await app.StartAsync();
		return app;
	}

	private static HttpClient ClientFor(WebApplication app, string? role)
	{
		HttpClient client = app.GetTestClient();
		if (role is not null)
		{
			client.DefaultRequestHeaders.Add("Test-Role", role);
		}

		return client;
	}

	private static HttpRequestMessage RequestFor(string method, string path)
	{
		HttpRequestMessage request = new(new HttpMethod(method), path);
		if (path == CallbackPath)
		{
			request.Content = JsonContent.Create(new { code = "auth-code-7c1d", state = "state-value-5e2a" });
		}

		return request;
	}

	[Theory]
	[InlineData("POST", AuthorizationPath, null, 401)]
	[InlineData("POST", CallbackPath, null, 401)]
	[InlineData("DELETE", ConnectionPath, null, 401)]
	[InlineData("POST", AuthorizationPath, "User", 403)]
	[InlineData("POST", CallbackPath, "User", 403)]
	[InlineData("DELETE", ConnectionPath, "User", 403)]
	public async Task Endpoint_CallerIsNotAnAdmin_IsRefusedAndNothingIsStartedCompletedOrDisconnected(
		string method, string path, string? role, int expectedStatus)
	{
		// Arrange
		await using WebApplication app = await StartAsync();
		using HttpClient client = ClientFor(app, role);

		// Act
		using HttpResponseMessage response = await client.SendAsync(RequestFor(method, path));

		// Assert
		((int)response.StatusCode).Should().Be(expectedStatus);
		_mediator.Invocations.Should().BeEmpty();
	}

	[Fact]
	public async Task StartAuthorization_Admin_Returns200WithAuthorizationUrl()
	{
		// Arrange
		const string url = "https://app.ynab.com/oauth/authorize?client_id=client-id-1&redirect_uri=https%3A%2F%2Freceipts.example%2Fsettings%2Fynab%2Fcallback&response_type=code&state=state-value-5e2a&code_challenge=challenge-1&code_challenge_method=S256";
		_mediator.Setup(m => m.Send(new StartYnabAuthorizationCommand("owner"), It.IsAny<CancellationToken>())).ReturnsAsync(url);
		await using WebApplication app = await StartAsync();
		using HttpClient client = ClientFor(app, "Admin");

		// Act
		using HttpResponseMessage response = await client.PostAsync(AuthorizationPath, content: null);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		body.RootElement.GetProperty("authorizationUrl").GetString().Should().Be(url);
	}

	[Fact]
	public async Task StartAuthorization_OAuthNotConfigured_Returns409ProblemDocument()
	{
		// Arrange
		_mediator.Setup(m => m.Send(new StartYnabAuthorizationCommand("owner"), It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);
		await using WebApplication app = await StartAsync();
		using HttpClient client = ClientFor(app, "Admin");

		// Act
		using HttpResponseMessage response = await client.PostAsync(AuthorizationPath, content: null);

		// Assert
		using JsonDocument problem = await ReadProblemAsync(response, HttpStatusCode.Conflict);
		problem.RootElement.GetProperty("detail").GetString().Should().Be("YNAB OAuth is not configured on this deployment");
	}

	[Fact]
	public async Task Callback_Connected_Returns200WithTheConnectionStatusAndNoTokenLikeMember()
	{
		// Arrange
		DateTimeOffset connectedAt = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
		_mediator
			.Setup(m => m.Send(
				new CompleteYnabAuthorizationCommand("owner", "auth-code-7c1d", "state-value-5e2a"), It.IsAny<CancellationToken>()))
			.ReturnsAsync(YnabAuthorizationCompletion.Connected);
		_mediator.Setup(m => m.Send(It.IsAny<GetYnabConnectionStatusQuery>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabConnectionStatus(
				true, true, null, new YnabCredentialState(YnabCredentialKind.OAuth, OAuthAvailable: true, OAuthConnectedAtUtc: connectedAt)));
		await using WebApplication app = await StartAsync();
		using HttpClient client = ClientFor(app, "Admin");

		// Act
		using HttpResponseMessage response = await client.SendAsync(RequestFor("POST", CallbackPath));

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		string json = await response.Content.ReadAsStringAsync();
		using JsonDocument body = JsonDocument.Parse(json);
		body.RootElement.GetProperty("credentialSource").GetString().Should().Be("oauth");
		body.RootElement.GetProperty("isConfigured").GetBoolean().Should().BeTrue();
		body.RootElement.GetProperty("isConnected").GetBoolean().Should().BeTrue();
		body.RootElement.GetProperty("reconnectRequired").GetBoolean().Should().BeFalse();
		body.RootElement.GetProperty("oauthAvailable").GetBoolean().Should().BeTrue();
		body.RootElement.GetProperty("oauthConnectedAtUtc").GetDateTimeOffset().Should().Be(connectedAt);
		body.RootElement.EnumerateObject().Select(member => member.Name).Should().BeEquivalentTo(
			"isConfigured", "isConnected", "credentialSource", "oauthAvailable", "reconnectRequired",
			"oauthConnectedAtUtc", "lastSuccessfulSyncUtc");
		json.Should().NotContain("auth-code-7c1d").And.NotContain("state-value-5e2a");
	}

	[Theory]
	[InlineData(YnabAuthorizationCompletion.AttemptNotValid, "authorization attempt is not valid")]
	[InlineData(YnabAuthorizationCompletion.RejectedByYnab, "YNAB rejected the authorization")]
	public async Task Callback_Refused_Returns400ProblemDocumentNamingTheReason(YnabAuthorizationCompletion completion, string expectedDetail)
	{
		// Arrange
		_mediator
			.Setup(m => m.Send(
				new CompleteYnabAuthorizationCommand("owner", "auth-code-7c1d", "state-value-5e2a"), It.IsAny<CancellationToken>()))
			.ReturnsAsync(completion);
		await using WebApplication app = await StartAsync();
		using HttpClient client = ClientFor(app, "Admin");

		// Act
		using HttpResponseMessage response = await client.SendAsync(RequestFor("POST", CallbackPath));

		// Assert
		using JsonDocument problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest);
		problem.RootElement.GetProperty("detail").GetString().Should().Contain(expectedDetail);
	}

	[Fact]
	public async Task Callback_YnabUnavailable_ReturnsBodiless503()
	{
		// Arrange
		_mediator
			.Setup(m => m.Send(
				new CompleteYnabAuthorizationCommand("owner", "auth-code-7c1d", "state-value-5e2a"), It.IsAny<CancellationToken>()))
			.ReturnsAsync(YnabAuthorizationCompletion.YnabUnavailable);
		await using WebApplication app = await StartAsync();
		using HttpClient client = ClientFor(app, "Admin");

		// Act
		using HttpResponseMessage response = await client.SendAsync(RequestFor("POST", CallbackPath));

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
		(await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
	}

	[Theory]
	[InlineData("""{"state":"state-value-5e2a"}""")]
	[InlineData("""{"code":"auth-code-7c1d"}""")]
	[InlineData("""{"code":"","state":"state-value-5e2a"}""")]
	public async Task Callback_CodeOrStateMissing_Returns400WithoutCompletingAnything(string json)
	{
		// Arrange
		await using WebApplication app = await StartAsync();
		using HttpClient client = ClientFor(app, "Admin");
		using StringContent content = new(json, System.Text.Encoding.UTF8, "application/json");

		// Act
		using HttpResponseMessage response = await client.PostAsync(CallbackPath, content);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		_mediator.Invocations.Should().BeEmpty();
	}

	[Theory]
	[InlineData(YnabRemoteRevocation.Succeeded, "succeeded")]
	[InlineData(YnabRemoteRevocation.Failed, "failed")]
	[InlineData(YnabRemoteRevocation.NotAttempted, "notAttempted")]
	public async Task Disconnect_Admin_Returns200NamingTheRemoteRevocation(YnabRemoteRevocation revocation, string expected)
	{
		// Arrange
		_mediator.Setup(m => m.Send(It.IsAny<DisconnectYnabCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(revocation);
		await using WebApplication app = await StartAsync();
		using HttpClient client = ClientFor(app, "Admin");

		// Act
		using HttpResponseMessage response = await client.DeleteAsync(ConnectionPath);

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		body.RootElement.GetProperty("remoteRevocation").GetString().Should().Be(expected);
	}

	private static async Task<JsonDocument> ReadProblemAsync(HttpResponseMessage response, HttpStatusCode status)
	{
		response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		document.RootElement.GetProperty("status").GetInt32().Should().Be((int)status);
		return document;
	}

	private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
		: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
	{
		protected override Task<AuthenticateResult> HandleAuthenticateAsync()
		{
			if (!Request.Headers.TryGetValue("Test-Role", out var role))
			{
				return Task.FromResult(AuthenticateResult.NoResult());
			}

			ClaimsPrincipal principal = new(new ClaimsIdentity(
				[new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Role, role.ToString())], Scheme.Name));
			return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
		}
	}
}
