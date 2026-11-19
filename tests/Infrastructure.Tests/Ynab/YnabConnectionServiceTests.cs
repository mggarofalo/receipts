using System.Security.Cryptography;
using System.Text;
using Application.Models.Ynab;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Ynab;
using Infrastructure.Ynab.OAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Infrastructure.Tests.Ynab;

public class YnabConnectionServiceTests
{
	private const string ClientId = "client-id-1";
	private const string ClientSecret = "client-secret-9f3c";
	private const string RedirectUri = "https://receipts.example/settings/ynab/callback";
	private const string Admin = "admin-1";
	private const string OtherAdmin = "admin-2";
	private const string Code = "auth-code-7c1d";
	private const string AccessToken = "access-token-one-a1";
	private const string RefreshToken = "refresh-token-one-r1";

	private static readonly DateTimeOffset Start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
	private static readonly YnabOAuthSettings Settings = new(ClientId, ClientSecret, RedirectUri);
	private static readonly YnabOAuthTokens Tokens = new(AccessToken, RefreshToken, Start.AddHours(2));

	private readonly FakeTimeProvider _time = new(Start);
	private readonly Mock<IYnabOAuthClient> _oauthClient = new(MockBehavior.Strict);
	private readonly Mock<IYnabOAuthConnectionStore> _connectionStore = new();
	private readonly CapturingLogger<YnabConnectionService> _logger = new();
	private readonly IConfigurationRoot _configuration;
	private readonly YnabConnectionService _service;

	// What the last authorization address carried, as the browser would bring it back.
	private string _issuedState = string.Empty;
	private string _issuedChallenge = string.Empty;

	public YnabConnectionServiceTests()
	{
		_configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["YNAB_OAUTH_CLIENT_ID"] = ClientId,
				["YNAB_OAUTH_CLIENT_SECRET"] = ClientSecret,
				["YNAB_OAUTH_REDIRECT_URI"] = RedirectUri,
			})
			.Build();

		_oauthClient
			.Setup(c => c.BuildAuthorizationUrl(Settings, It.IsAny<string>(), It.IsAny<string>()))
			.Returns((YnabOAuthSettings _, string state, string challenge) =>
			{
				_issuedState = state;
				_issuedChallenge = challenge;
				return $"https://app.ynab.com/oauth/authorize?state={state}";
			});

		_service = new YnabConnectionService(
			new YnabOAuthSettingsProvider(_configuration),
			new YnabAuthorizationAttemptStore(_time),
			_oauthClient.Object,
			_connectionStore.Object,
			_logger);
	}

	private void RemoveOAuthSettings() => _configuration["YNAB_OAUTH_CLIENT_SECRET"] = null;

	private void RestoreOAuthSettings() => _configuration["YNAB_OAUTH_CLIENT_SECRET"] = ClientSecret;

	// The verifier is never shown to the test; it is recognised by hashing to the issued challenge.
	private bool MatchesIssuedChallenge(string codeVerifier) =>
		Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)))
			.TrimEnd('=').Replace('+', '-').Replace('/', '_') == _issuedChallenge;

	private void SetupExchange(string code, params YnabTokenResult[] results)
	{
		Moq.Language.ISetupSequentialResult<Task<YnabTokenResult>> setup = _oauthClient.SetupSequence(c => c.ExchangeCodeAsync(
			Settings, code, It.Is<string>(verifier => MatchesIssuedChallenge(verifier)), It.IsAny<CancellationToken>()));
		foreach (YnabTokenResult result in results)
		{
			setup = setup.ReturnsAsync(result);
		}
	}

	private void VerifyExchanges(Times times) =>
		_oauthClient.Verify(
			c => c.ExchangeCodeAsync(It.IsAny<YnabOAuthSettings>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
			times);

	private void VerifyStored(Times times) =>
		_connectionStore.Verify(
			s => s.StoreNewAsync(It.IsAny<YnabOAuthTokens>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), times);

	// The store hands the disconnect step the refresh token it holds, then removes the connection.
	private void SetupStoredRefreshToken(string? refreshToken) =>
		_connectionStore
			.Setup(s => s.RemoveAsync(
				It.IsAny<Func<string?, CancellationToken, Task<YnabRemoteRevocation>>>(), It.IsAny<CancellationToken>()))
			.Returns((Func<string?, CancellationToken, Task<YnabRemoteRevocation>> beforeRemove, CancellationToken token) =>
				beforeRemove(refreshToken, token));

	private void VerifyRemovedOnce() =>
		_connectionStore.Verify(
			s => s.RemoveAsync(
				It.IsAny<Func<string?, CancellationToken, Task<YnabRemoteRevocation>>>(), It.IsAny<CancellationToken>()),
			Times.Once);

	private void AssertNoSecretLogged()
	{
		string logged = _logger.AllText;
		foreach (string secret in new[] { Code, _issuedState, AccessToken, RefreshToken, ClientSecret }.Where(value => value.Length > 0))
		{
			logged.Should().NotContain(secret);
		}
	}

	#region Start

	[Fact]
	public async Task StartAuthorizationAsync_WithoutOAuthSettings_ReturnsNullAndCreatesNoAttempt()
	{
		// Arrange
		RemoveOAuthSettings();

		// Act
		string? url = await _service.StartAuthorizationAsync(Admin, CancellationToken.None);

		// Assert
		url.Should().BeNull();
		_oauthClient.Verify(
			c => c.BuildAuthorizationUrl(It.IsAny<YnabOAuthSettings>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
	}

	[Fact]
	public async Task StartAuthorizationAsync_WithOAuthSettings_ReturnsTheAddressForANewAttempt()
	{
		// Act
		string? url = await _service.StartAuthorizationAsync(Admin, CancellationToken.None);

		// Assert
		url.Should().Be($"https://app.ynab.com/oauth/authorize?state={_issuedState}");
		_issuedState.Should().NotBeNullOrWhiteSpace();
		_issuedChallenge.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task StartAuthorizationAsync_CalledTwice_IssuesADifferentStateEachTime()
	{
		// Act
		await _service.StartAuthorizationAsync(Admin, CancellationToken.None);
		string firstState = _issuedState;
		await _service.StartAuthorizationAsync(Admin, CancellationToken.None);

		// Assert
		_issuedState.Should().NotBe(firstState);
	}

	#endregion

	#region Complete

	[Fact]
	public async Task CompleteAuthorizationAsync_CodeAccepted_StoresTheConnectionForTheAdmin()
	{
		// Arrange
		await _service.StartAuthorizationAsync(Admin, CancellationToken.None);
		SetupExchange(Code, new YnabTokenResult(YnabTokenOutcome.Success, Tokens));

		// Act
		YnabAuthorizationCompletion completion =
			await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);

		// Assert
		completion.Should().Be(YnabAuthorizationCompletion.Connected);
		_connectionStore.Verify(s => s.StoreNewAsync(Tokens, Admin, It.IsAny<CancellationToken>()), Times.Once);
		VerifyExchanges(Times.Once());
		AssertNoSecretLogged();
	}

	[Fact]
	public async Task CompleteAuthorizationAsync_AfterItCompleted_CannotBeUsedAgain()
	{
		// Arrange
		await _service.StartAuthorizationAsync(Admin, CancellationToken.None);
		SetupExchange(Code, new YnabTokenResult(YnabTokenOutcome.Success, Tokens));
		await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);

		// Act
		YnabAuthorizationCompletion again =
			await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);

		// Assert
		again.Should().Be(YnabAuthorizationCompletion.AttemptNotValid);
		VerifyExchanges(Times.Once());
		VerifyStored(Times.Once());
	}

	[Fact]
	public async Task CompleteAuthorizationAsync_StateNeverIssued_IsNotValidAndNothingIsSentToYnab()
	{
		// Arrange
		await _service.StartAuthorizationAsync(Admin, CancellationToken.None);

		// Act
		YnabAuthorizationCompletion completion =
			await _service.CompleteAuthorizationAsync(Admin, Code, "state-that-was-never-issued", CancellationToken.None);

		// Assert
		completion.Should().Be(YnabAuthorizationCompletion.AttemptNotValid);
		VerifyExchanges(Times.Never());
		VerifyStored(Times.Never());
	}

	[Fact]
	public async Task CompleteAuthorizationAsync_AfterTenMinutes_IsNotValidAndNothingIsSentToYnab()
	{
		// Arrange
		await _service.StartAuthorizationAsync(Admin, CancellationToken.None);
		_time.Advance(TimeSpan.FromMinutes(10));

		// Act
		YnabAuthorizationCompletion completion =
			await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);

		// Assert
		completion.Should().Be(YnabAuthorizationCompletion.AttemptNotValid);
		VerifyExchanges(Times.Never());
		VerifyStored(Times.Never());
	}

	[Fact]
	public async Task CompleteAuthorizationAsync_ByADifferentAdmin_IsNotValidAndLeavesTheAttemptForItsOwner()
	{
		// Arrange
		await _service.StartAuthorizationAsync(Admin, CancellationToken.None);
		SetupExchange(Code, new YnabTokenResult(YnabTokenOutcome.Success, Tokens));

		// Act
		YnabAuthorizationCompletion byOther =
			await _service.CompleteAuthorizationAsync(OtherAdmin, Code, _issuedState, CancellationToken.None);

		// Assert
		byOther.Should().Be(YnabAuthorizationCompletion.AttemptNotValid);
		VerifyExchanges(Times.Never());
		VerifyStored(Times.Never());

		YnabAuthorizationCompletion byOwner =
			await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);
		byOwner.Should().Be(YnabAuthorizationCompletion.Connected);
		_connectionStore.Verify(s => s.StoreNewAsync(Tokens, Admin, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Theory]
	[InlineData(YnabTokenOutcome.InvalidGrant, "invalid_grant")]
	[InlineData(YnabTokenOutcome.Rejected, "invalid_client")]
	public async Task CompleteAuthorizationAsync_YnabRejectsTheCode_IsRejectedAndTheAttemptIsSpent(
		YnabTokenOutcome outcome, string error)
	{
		// Arrange
		await _service.StartAuthorizationAsync(Admin, CancellationToken.None);
		SetupExchange(Code, new YnabTokenResult(outcome, Error: error));

		// Act
		YnabAuthorizationCompletion completion =
			await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);
		YnabAuthorizationCompletion again =
			await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);

		// Assert
		completion.Should().Be(YnabAuthorizationCompletion.RejectedByYnab);
		again.Should().Be(YnabAuthorizationCompletion.AttemptNotValid);
		VerifyExchanges(Times.Once());
		VerifyStored(Times.Never());
		AssertNoSecretLogged();
	}

	[Fact]
	public async Task CompleteAuthorizationAsync_YnabUnavailable_LeavesTheSameAttemptCompletable()
	{
		// Arrange
		await _service.StartAuthorizationAsync(Admin, CancellationToken.None);
		SetupExchange(
			Code,
			new YnabTokenResult(YnabTokenOutcome.Unavailable),
			new YnabTokenResult(YnabTokenOutcome.Success, Tokens));

		// Act
		YnabAuthorizationCompletion first =
			await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);
		int storedAfterFirst = _connectionStore.Invocations.Count;
		YnabAuthorizationCompletion second =
			await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);

		// Assert
		first.Should().Be(YnabAuthorizationCompletion.YnabUnavailable);
		storedAfterFirst.Should().Be(0, "nothing is stored while YNAB has not answered");
		second.Should().Be(YnabAuthorizationCompletion.Connected);
		VerifyExchanges(Times.Exactly(2));
		_connectionStore.Verify(s => s.StoreNewAsync(Tokens, Admin, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task CompleteAuthorizationAsync_YnabUnavailable_DoesNotOutliveTheOriginalTenMinutes()
	{
		// Arrange
		await _service.StartAuthorizationAsync(Admin, CancellationToken.None);
		SetupExchange(Code, new YnabTokenResult(YnabTokenOutcome.Unavailable));
		_time.Advance(TimeSpan.FromMinutes(9));
		await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);

		// Act
		_time.Advance(TimeSpan.FromMinutes(1));
		YnabAuthorizationCompletion late =
			await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);

		// Assert
		late.Should().Be(YnabAuthorizationCompletion.AttemptNotValid);
		VerifyExchanges(Times.Once());
	}

	[Fact]
	public async Task CompleteAuthorizationAsync_ExchangeThrows_LeavesTheSameAttemptCompletable()
	{
		// Arrange
		await _service.StartAuthorizationAsync(Admin, CancellationToken.None);
		_oauthClient
			.SetupSequence(c => c.ExchangeCodeAsync(
				Settings, Code, It.Is<string>(verifier => MatchesIssuedChallenge(verifier)), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new OperationCanceledException("the browser went away"))
			.ReturnsAsync(new YnabTokenResult(YnabTokenOutcome.Success, Tokens));

		// Act
		Func<Task> first = () => _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);
		await first.Should().ThrowAsync<OperationCanceledException>();
		YnabAuthorizationCompletion second =
			await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);

		// Assert
		second.Should().Be(YnabAuthorizationCompletion.Connected);
	}

	[Fact]
	public async Task CompleteAuthorizationAsync_SettingsWentMissing_IsNotValidButDoesNotSpendTheAttempt()
	{
		// Arrange
		await _service.StartAuthorizationAsync(Admin, CancellationToken.None);
		SetupExchange(Code, new YnabTokenResult(YnabTokenOutcome.Success, Tokens));
		RemoveOAuthSettings();

		// Act
		YnabAuthorizationCompletion withoutSettings =
			await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);

		// Assert
		withoutSettings.Should().Be(YnabAuthorizationCompletion.AttemptNotValid);
		VerifyExchanges(Times.Never());

		RestoreOAuthSettings();
		YnabAuthorizationCompletion withSettings =
			await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);
		withSettings.Should().Be(YnabAuthorizationCompletion.Connected);
	}

	[Fact]
	public async Task CompleteAuthorizationAsync_ConnectionCannotBeStored_FailsAndTheAttemptIsSpent()
	{
		// Arrange — YNAB has consumed the code, so trying the same attempt again cannot work.
		await _service.StartAuthorizationAsync(Admin, CancellationToken.None);
		SetupExchange(Code, new YnabTokenResult(YnabTokenOutcome.Success, Tokens));
		_connectionStore.Setup(s => s.StoreNewAsync(Tokens, Admin, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("database unavailable"));

		// Act
		Func<Task> act = () => _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("database unavailable");
		YnabAuthorizationCompletion again =
			await _service.CompleteAuthorizationAsync(Admin, Code, _issuedState, CancellationToken.None);
		again.Should().Be(YnabAuthorizationCompletion.AttemptNotValid);
		VerifyExchanges(Times.Once());
		AssertNoSecretLogged();
	}

	#endregion

	#region Disconnect

	[Fact]
	public async Task DisconnectAsync_YnabAcceptsTheRevocation_ReportsSucceededAndRemovesTheConnection()
	{
		// Arrange
		SetupStoredRefreshToken(RefreshToken);
		_oauthClient.Setup(c => c.RevokeAsync(Settings, RefreshToken, It.IsAny<CancellationToken>())).ReturnsAsync(true);

		// Act
		YnabRemoteRevocation revocation = await _service.DisconnectAsync(CancellationToken.None);

		// Assert
		revocation.Should().Be(YnabRemoteRevocation.Succeeded);
		_oauthClient.Verify(c => c.RevokeAsync(Settings, RefreshToken, It.IsAny<CancellationToken>()), Times.Once);
		VerifyRemovedOnce();
		AssertNoSecretLogged();
	}

	[Fact]
	public async Task DisconnectAsync_YnabRefusesTheRevocation_ReportsFailedAndStillRemovesTheConnection()
	{
		// Arrange
		SetupStoredRefreshToken(RefreshToken);
		_oauthClient.Setup(c => c.RevokeAsync(Settings, RefreshToken, It.IsAny<CancellationToken>())).ReturnsAsync(false);

		// Act
		YnabRemoteRevocation revocation = await _service.DisconnectAsync(CancellationToken.None);

		// Assert
		revocation.Should().Be(YnabRemoteRevocation.Failed);
		VerifyRemovedOnce();
	}

	[Fact]
	public async Task DisconnectAsync_RevocationThrows_ReportsFailedAndStillRemovesTheConnection()
	{
		// Arrange
		SetupStoredRefreshToken(RefreshToken);
		_oauthClient.Setup(c => c.RevokeAsync(Settings, RefreshToken, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("connection reset"));

		// Act
		YnabRemoteRevocation revocation = await _service.DisconnectAsync(CancellationToken.None);

		// Assert
		revocation.Should().Be(YnabRemoteRevocation.Failed);
		VerifyRemovedOnce();
		AssertNoSecretLogged();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DisconnectAsync_RevocationFailsAgainstTheRealStore_ReportsFailedAndDeletesTheRow(bool revokeThrows)
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(AccessToken, RefreshToken, Start.AddHours(2));
		_oauthClient
			.Setup(c => c.RevokeAsync(YnabCredentialSourceHarness.Settings, RefreshToken, It.IsAny<CancellationToken>()))
			.Returns(() => revokeThrows
				? Task.FromException<bool>(new HttpRequestException("connection reset"))
				: Task.FromResult(false));

		YnabConnectionService service = new(
			harness.SettingsProvider, new YnabAuthorizationAttemptStore(_time), _oauthClient.Object, harness.Source, _logger);

		// Act
		YnabRemoteRevocation revocation = await service.DisconnectAsync(CancellationToken.None);

		// Assert
		revocation.Should().Be(YnabRemoteRevocation.Failed);
		(await harness.Repository.ReadRowAsync()).Should().BeNull();
		(await harness.Source.GetStateAsync(CancellationToken.None)).Kind.Should().Be(YnabCredentialKind.None);
	}

	[Fact]
	public async Task DisconnectAsync_RevocationIsCancelled_StillDeletesTheRow()
	{
		// Arrange — the Admin's request is aborted while YNAB is being asked to revoke.
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(AccessToken, RefreshToken, Start.AddHours(2));
		_oauthClient
			.Setup(c => c.RevokeAsync(YnabCredentialSourceHarness.Settings, RefreshToken, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new OperationCanceledException("request aborted"));
		YnabConnectionService service = new(
			harness.SettingsProvider, new YnabAuthorizationAttemptStore(_time), _oauthClient.Object, harness.Source, _logger);

		// Act
		Func<Task> act = () => service.DisconnectAsync(CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<OperationCanceledException>();
		(await harness.Repository.ReadRowAsync()).Should().BeNull();
		(await harness.Source.GetStateAsync(CancellationToken.None)).Kind.Should().Be(YnabCredentialKind.None);
	}

	[Fact]
	public async Task DisconnectAsync_NothingConnected_ReportsNotAttemptedAndStillClearsTheStore()
	{
		// Arrange
		SetupStoredRefreshToken(null);

		// Act
		YnabRemoteRevocation revocation = await _service.DisconnectAsync(CancellationToken.None);

		// Assert
		revocation.Should().Be(YnabRemoteRevocation.NotAttempted);
		_oauthClient.Verify(
			c => c.RevokeAsync(It.IsAny<YnabOAuthSettings>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
		VerifyRemovedOnce();
	}

	[Fact]
	public async Task DisconnectAsync_WithoutOAuthSettings_ReportsNotAttemptedAndStillRemovesTheConnection()
	{
		// Arrange — revocation needs the client secret, which this deployment no longer has.
		SetupStoredRefreshToken(RefreshToken);
		RemoveOAuthSettings();

		// Act
		YnabRemoteRevocation revocation = await _service.DisconnectAsync(CancellationToken.None);

		// Assert
		revocation.Should().Be(YnabRemoteRevocation.NotAttempted);
		_oauthClient.Verify(
			c => c.RevokeAsync(It.IsAny<YnabOAuthSettings>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
		VerifyRemovedOnce();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DisconnectAsync_UnreadableOrDiscardedTokensInTheRealStore_ReportsNotAttemptedAndDeletesTheRow(bool reconnectRequired)
	{
		// Arrange — the real credential source as the store, over a row this host cannot use.
		using YnabCredentialSourceHarness harness = new(pat: "personal-access-token-p1");
		if (reconnectRequired)
		{
			await harness.SeedReconnectRequiredAsync();
		}
		else
		{
			await harness.Repository.Inner.SaveConnectedAsync(
				new YnabOAuthTokenProtector(new EphemeralDataProtectionProvider()).Protect(AccessToken, RefreshToken),
				Start.AddHours(2), Admin, Start, CancellationToken.None);
		}

		YnabConnectionService service = new(
			harness.SettingsProvider, new YnabAuthorizationAttemptStore(_time), _oauthClient.Object, harness.Source, _logger);

		// Act
		YnabRemoteRevocation revocation = await service.DisconnectAsync(CancellationToken.None);

		// Assert
		revocation.Should().Be(YnabRemoteRevocation.NotAttempted);
		YnabOAuthConnectionEntity? row = await harness.Repository.ReadRowAsync();
		row.Should().BeNull();
		(await harness.Source.GetStateAsync(CancellationToken.None)).Kind.Should().Be(YnabCredentialKind.PersonalAccessToken);
	}

	[Fact]
	public async Task DisconnectAsync_ConnectedInTheRealStore_RevokesTheStoredRefreshTokenAndDeletesTheRow()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(AccessToken, RefreshToken, Start.AddHours(2));
		_oauthClient
			.Setup(c => c.RevokeAsync(YnabCredentialSourceHarness.Settings, RefreshToken, It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);
		YnabConnectionService service = new(
			harness.SettingsProvider, new YnabAuthorizationAttemptStore(_time), _oauthClient.Object, harness.Source, _logger);

		// Act
		YnabRemoteRevocation revocation = await service.DisconnectAsync(CancellationToken.None);

		// Assert
		revocation.Should().Be(YnabRemoteRevocation.Succeeded);
		(await harness.Repository.ReadRowAsync()).Should().BeNull();
		(await harness.Source.GetCredentialAsync(CancellationToken.None)).Should().BeNull();
	}

	#endregion
}
