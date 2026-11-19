using System.Net;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Ynab;
using Infrastructure.Ynab.OAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Infrastructure.Tests.Ynab;

/// <summary>
/// The OAuth half of the credential source: credential order, the in-memory access token,
/// renewal and its outcomes. The <c>YNAB_PAT</c>-only cases live in
/// <c>Services/YnabCredentialSourceTests</c>.
/// </summary>
public class YnabCredentialSourceOAuthTests
{
	private const string Pat = "personal-access-token-p1";
	private const string Access1 = "access-token-one-a1";
	private const string Refresh1 = "refresh-token-one-r1";
	private const string Access2 = "access-token-two-a2";
	private const string Refresh2 = "refresh-token-two-r2";
	private const string Access3 = "access-token-three-a3";
	private const string Refresh3 = "refresh-token-three-r3";

	private static readonly DateTimeOffset Start = YnabCredentialSourceHarness.Start;
	private static readonly YnabOAuthSettings Settings = YnabCredentialSourceHarness.Settings;

	private static YnabTokenResult Success(string accessToken, string refreshToken, DateTimeOffset expiresAtUtc) =>
		new(YnabTokenOutcome.Success, new YnabOAuthTokens(accessToken, refreshToken, expiresAtUtc));

	private static void SetupRefresh(YnabCredentialSourceHarness harness, string refreshToken, YnabTokenResult result) =>
		harness.OAuthClient
			.Setup(c => c.RefreshAsync(Settings, refreshToken, It.IsAny<CancellationToken>()))
			.ReturnsAsync(result);

	private static void VerifyRefreshes(YnabCredentialSourceHarness harness, Times times) =>
		harness.OAuthClient.Verify(
			c => c.RefreshAsync(It.IsAny<YnabOAuthSettings>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), times);

	#region Credential order

	[Theory]
	[InlineData(Pat)]
	[InlineData(null)]
	public async Task StoredConnection_IsUsedWhetherOrNotAPatIsSet(string? pat)
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new(pat);
		YnabOAuthConnectionEntity row = await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));

		// Act
		YnabCredentialState state = await harness.Source.GetStateAsync(CancellationToken.None);
		YnabBearerCredential? credential = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		state.Kind.Should().Be(YnabCredentialKind.OAuth);
		state.IsUsable.Should().BeTrue();
		state.OAuthConnectedAtUtc.Should().Be(row.ConnectedAtUtc);
		credential!.Token.Should().Be(Access1);
		VerifyRefreshes(harness, Times.Never());
	}

	[Fact]
	public async Task NoConnection_PatSet_UsesThePat()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new(Pat);

		// Act
		YnabCredentialState state = await harness.Source.GetStateAsync(CancellationToken.None);
		YnabBearerCredential? credential = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		state.Kind.Should().Be(YnabCredentialKind.PersonalAccessToken);
		state.OAuthConnectedAtUtc.Should().BeNull();
		credential!.Token.Should().Be(Pat);
	}

	[Fact]
	public async Task NoConnection_NoPat_HasNoCredential()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new(pat: null);

		// Act
		YnabCredentialState state = await harness.Source.GetStateAsync(CancellationToken.None);
		YnabBearerCredential? credential = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		state.Kind.Should().Be(YnabCredentialKind.None);
		state.IsUsable.Should().BeFalse();
		credential.Should().BeNull();
	}

	[Theory]
	[InlineData(Pat)]
	[InlineData(null)]
	public async Task ReconnectRequired_NeverFallsBackToThePat(string? pat)
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new(pat);
		await harness.SeedReconnectRequiredAsync();

		// Act
		YnabCredentialState state = await harness.Source.GetStateAsync(CancellationToken.None);
		YnabBearerCredential? credential = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		state.Kind.Should().Be(YnabCredentialKind.OAuthReconnectRequired);
		state.IsUsable.Should().BeFalse();
		state.OAuthConnectedAtUtc.Should().BeNull();
		credential.Should().BeNull();
	}

	[Theory]
	[InlineData(true, YnabCredentialKind.None)]
	[InlineData(false, YnabCredentialKind.None)]
	[InlineData(true, YnabCredentialKind.OAuth)]
	[InlineData(false, YnabCredentialKind.OAuth)]
	[InlineData(true, YnabCredentialKind.OAuthReconnectRequired)]
	[InlineData(false, YnabCredentialKind.OAuthReconnectRequired)]
	public async Task GetStateAsync_ReportsWhetherOAuthCanBeStartedHere(bool oauthSettings, YnabCredentialKind kind)
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new(pat: null, oauthSettings);
		if (kind == YnabCredentialKind.OAuth)
		{
			await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		}
		else if (kind == YnabCredentialKind.OAuthReconnectRequired)
		{
			await harness.SeedReconnectRequiredAsync();
		}

		// Act
		YnabCredentialState state = await harness.Source.GetStateAsync(CancellationToken.None);

		// Assert
		state.Kind.Should().Be(kind);
		state.OAuthAvailable.Should().Be(oauthSettings);
	}

	#endregion

	#region In-memory access token

	[Fact]
	public async Task FreshToken_RepeatedCalls_ReadTheStoredConnectionOnce()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));

		// Act
		List<string> tokens = [];
		for (int i = 0; i < 5; i++)
		{
			tokens.Add((await harness.Source.GetCredentialAsync(CancellationToken.None))!.Token);
			await harness.Source.GetStateAsync(CancellationToken.None);
		}

		// Assert
		tokens.Should().AllBe(Access1);
		harness.Repository.GetCalls.Should().Be(1);
		VerifyRefreshes(harness, Times.Never());
	}

	[Fact]
	public async Task NoConnection_RepeatedCalls_ReadTheStoredConnectionOnce()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new(Pat);

		// Act
		for (int i = 0; i < 5; i++)
		{
			await harness.Source.GetCredentialAsync(CancellationToken.None);
		}

		// Assert
		harness.Repository.GetCalls.Should().Be(1);
	}

	[Fact]
	public async Task TokenServedFromMemory_IsRenewedOnceTheClockReachesTheSkew()
	{
		// Arrange — fresh when first read, then time passes without any further read.
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		YnabBearerCredential? before = await harness.Source.GetCredentialAsync(CancellationToken.None);
		SetupRefresh(harness, Refresh1, Success(Access2, Refresh2, Start.AddHours(4)));

		// Act
		harness.Time.Advance(TimeSpan.FromHours(2) - TimeSpan.FromMinutes(5));
		YnabBearerCredential? after = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		before!.Token.Should().Be(Access1);
		after!.Token.Should().Be(Access2);
		VerifyRefreshes(harness, Times.Once());
	}

	#endregion

	#region Renewal

	[Theory]
	[InlineData(301, false)]
	[InlineData(300, true)]
	[InlineData(299, true)]
	[InlineData(0, true)]
	[InlineData(-3600, true)]
	public async Task GetCredentialAsync_RenewsOnlyWithinFiveMinutesOfExpiry(int secondsUntilExpiry, bool expectRenewal)
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddSeconds(secondsUntilExpiry));
		SetupRefresh(harness, Refresh1, Success(Access2, Refresh2, Start.AddHours(2)));

		// Act
		YnabBearerCredential? credential = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		credential!.Token.Should().Be(expectRenewal ? Access2 : Access1);
		VerifyRefreshes(harness, expectRenewal ? Times.Once() : Times.Never());
	}

	[Fact]
	public async Task Renewal_CommitsTheNewPairBeforeReturningIt()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new();
		YnabOAuthConnectionEntity seeded = await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		harness.Time.Advance(TimeSpan.FromSeconds(30));
		DateTimeOffset renewedAt = harness.Time.GetUtcNow();
		SetupRefresh(harness, Refresh1, Success(Access2, Refresh2, Start.AddHours(2)));

		// Act
		YnabBearerCredential? credential = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert — by the time the caller holds the token, the row already holds its pair.
		credential!.Token.Should().Be(Access2);
		(await harness.ReadStoredTokensAsync()).Should().Be((Access2, Refresh2));
		YnabOAuthConnectionEntity? row = await harness.Repository.ReadRowAsync();
		row!.AccessTokenExpiresAtUtc.Should().Be(Start.AddHours(2));
		row.RefreshedAtUtc.Should().Be(renewedAt);
		row.ConnectionId.Should().Be(seeded.ConnectionId);
		row.ConnectedAtUtc.Should().Be(seeded.ConnectedAtUtc);
		row.Status.Should().Be(YnabOAuthConnectionStatus.Connected);
	}

	[Fact]
	public async Task Renewal_ThenFurtherCalls_ServeTheRenewedTokenWithoutRenewingAgain()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		SetupRefresh(harness, Refresh1, Success(Access2, Refresh2, Start.AddHours(2)));
		await harness.Source.GetCredentialAsync(CancellationToken.None);
		int readsAfterRenewal = harness.Repository.GetCalls;

		// Act
		YnabBearerCredential? second = await harness.Source.GetCredentialAsync(CancellationToken.None);
		YnabBearerCredential? third = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		second!.Token.Should().Be(Access2);
		third!.Token.Should().Be(Access2);
		harness.Repository.GetCalls.Should().Be(readsAfterRenewal);
		VerifyRefreshes(harness, Times.Once());
	}

	[Fact]
	public async Task ConcurrentCallers_WhileTheTokenNeedsRenewing_CauseExactlyOneRefresh()
	{
		// Arrange — the token was fresh when first read and has since reached the skew, so every
		// caller below goes straight for a renewal. The refresh call blocks until released.
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		await harness.Source.GetCredentialAsync(CancellationToken.None);
		harness.Time.Advance(TimeSpan.FromHours(2));

		TaskCompletionSource<YnabTokenResult> refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource refreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int refreshCalls = 0;
		harness.OAuthClient
			.Setup(c => c.RefreshAsync(Settings, Refresh1, It.IsAny<CancellationToken>()))
			.Returns(() =>
			{
				Interlocked.Increment(ref refreshCalls);
				refreshStarted.TrySetResult();
				return refresh.Task;
			});

		// Act
		List<Task<YnabBearerCredential?>> callers = [];
		for (int i = 0; i < 25; i++)
		{
			callers.Add(Task.Run(() => harness.Source.GetCredentialAsync(CancellationToken.None).AsTask()));
		}

		await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
		for (int i = 0; i < 25; i++)
		{
			callers.Add(harness.Source.GetCredentialAsync(CancellationToken.None).AsTask());
		}

		callers.Should().OnlyContain(caller => !caller.IsCompleted, "nobody may proceed with a token that is still being renewed");
		refresh.SetResult(Success(Access2, Refresh2, harness.Time.GetUtcNow().AddHours(2)));
		YnabBearerCredential?[] credentials = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10));

		// Assert
		refreshCalls.Should().Be(1);
		credentials.Should().HaveCount(50).And.OnlyContain(credential => credential!.Token == Access2);
		(await harness.ReadStoredTokensAsync()).Should().Be((Access2, Refresh2));
	}

	[Fact]
	public async Task Renewal_CommitThrows_NeverHandsOutTheUnsavedToken()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		SetupRefresh(harness, Refresh1, Success(Access2, Refresh2, Start.AddHours(2)));
		harness.Repository.BeforeReplaceTokens = () => throw new InvalidOperationException("database unavailable");

		// Act
		Func<Task> act = async () => await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert — the failure surfaces, and the stored pair is still the one YNAB keeps valid.
		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("database unavailable");
		(await harness.ReadStoredTokensAsync()).Should().Be((Access1, Refresh1));

		// A later request renews again from the stored pair; the unsaved token never appears.
		harness.Repository.BeforeReplaceTokens = null;
		harness.OAuthClient.Reset();
		SetupRefresh(harness, Refresh1, Success(Access3, Refresh3, Start.AddHours(2)));
		YnabBearerCredential? later = await harness.Source.GetCredentialAsync(CancellationToken.None);
		later!.Token.Should().Be(Access3);
		(await harness.ReadStoredTokensAsync()).Should().Be((Access3, Refresh3));
	}

	[Fact]
	public async Task Renewal_RowChangedUnderneath_DropsTheRenewedPairAndFailsAsTemporary()
	{
		// Arrange — while YNAB is answering the refresh, an Admin completes a new authorization.
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		SetupRefresh(harness, Refresh1, Success(Access2, Refresh2, Start.AddHours(2)));
		YnabOAuthConnectionEntity? newer = null;
		harness.Repository.BeforeReplaceTokens = async () =>
			newer = await harness.SeedConnectedAsync(Access3, Refresh3, Start.AddHours(3), "admin-2");

		// Act
		Func<Task> act = async () => await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<YnabTokenRenewalUnavailableException>();
		(await harness.ReadStoredTokensAsync()).Should().Be((Access3, Refresh3), "the newer connection must not be overwritten");

		// The next request reads the row again and uses the newer connection, never the dropped pair.
		harness.Repository.BeforeReplaceTokens = null;
		YnabBearerCredential? later = await harness.Source.GetCredentialAsync(CancellationToken.None);
		later!.Token.Should().Be(Access3);
		later.CacheIdentity.Should().Be($"oauth-{newer!.ConnectionId:N}");
		VerifyRefreshes(harness, Times.Once());
		harness.Logger.AllText.Should().NotContain(Access2).And.NotContain(Refresh2);
	}

	[Fact]
	public async Task Renewal_InvalidGrant_MarksReconnectRequiredClearsTokensAndLogsTheSecurityEvent()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new(Pat);
		YnabOAuthConnectionEntity seeded = await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		harness.Time.Advance(TimeSpan.FromSeconds(30));
		DateTimeOffset rejectedAt = harness.Time.GetUtcNow();
		SetupRefresh(harness, Refresh1, new YnabTokenResult(YnabTokenOutcome.InvalidGrant, Error: "invalid_grant"));

		// Act
		Func<Task> act = async () => await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		(await act.Should().ThrowAsync<YnabAuthException>()).Which.Should().BeAssignableTo<IYnabDefiniteRejection>();

		YnabOAuthConnectionEntity? row = await harness.Repository.ReadRowAsync();
		row!.Status.Should().Be(YnabOAuthConnectionStatus.ReconnectRequired);
		row.ProtectedTokens.Should().BeNull();
		row.AccessTokenExpiresAtUtc.Should().BeNull();
		row.StatusChangedAtUtc.Should().Be(rejectedAt);

		CapturedLog warning = harness.Logger.Entries.Should()
			.ContainSingle(entry => entry.EventId == YnabCredentialSource.ConnectionRejectedEvent).Subject;
		warning.Level.Should().Be(LogLevel.Warning);
		warning.Message.Should().Contain(seeded.ConnectionId.ToString());
		harness.Logger.AllText.Should().NotContain(Access1).And.NotContain(Refresh1).And.NotContain(Pat);

		// Reconnect required stays visible afterwards, with no fallback to the PAT.
		YnabCredentialState state = await harness.Source.GetStateAsync(CancellationToken.None);
		state.Kind.Should().Be(YnabCredentialKind.OAuthReconnectRequired);
		(await harness.Source.GetCredentialAsync(CancellationToken.None)).Should().BeNull();
		VerifyRefreshes(harness, Times.Once());
	}

	[Fact]
	public async Task Renewal_ClientSettingsRefused_KeepsTheConnectionAndFailsAsUnauthenticated()
	{
		// Arrange — invalid_client is this deployment's misconfiguration, not a lost grant.
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		YnabOAuthConnectionEntity? before = await harness.Repository.ReadRowAsync();
		SetupRefresh(harness, Refresh1, new YnabTokenResult(YnabTokenOutcome.Rejected, Error: "invalid_client"));

		// Act
		Func<Task> act = async () => await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<YnabAuthException>();
		(await harness.Repository.ReadRowAsync()).Should().BeEquivalentTo(before);
		(await harness.Source.GetStateAsync(CancellationToken.None)).Kind.Should().Be(YnabCredentialKind.OAuth);
		harness.Logger.Entries.Should().NotContain(entry => entry.EventId == YnabCredentialSource.ConnectionRejectedEvent);
		harness.Logger.AllText.Should().NotContain(Access1).And.NotContain(Refresh1)
			.And.NotContain(YnabCredentialSourceHarness.ClientSecret);
	}

	[Fact]
	public async Task Renewal_YnabUnavailable_KeepsTheConnectionFailsAsTemporaryAndTriesAgainLater()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		YnabOAuthConnectionEntity? before = await harness.Repository.ReadRowAsync();
		harness.OAuthClient
			.SetupSequence(c => c.RefreshAsync(Settings, Refresh1, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabTokenResult(YnabTokenOutcome.Unavailable))
			.ReturnsAsync(Success(Access2, Refresh2, Start.AddHours(2)));

		// Act
		Func<Task> act = async () => await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert — the same kind of failure a YNAB outage raises, and known not to have been sent.
		YnabTokenRenewalUnavailableException exception =
			(await act.Should().ThrowAsync<YnabTokenRenewalUnavailableException>()).Which;
		exception.Should().BeAssignableTo<HttpRequestException>();
		exception.Should().BeAssignableTo<IYnabDefiniteRejection>();
		exception.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
		(await harness.Repository.ReadRowAsync()).Should().BeEquivalentTo(before);
		(await harness.Source.GetStateAsync(CancellationToken.None)).Kind.Should().Be(YnabCredentialKind.OAuth);

		// A later request, once the backoff has passed, tries the renewal again.
		harness.Time.Advance(YnabCredentialSource.RenewalBackoff);
		YnabBearerCredential? later = await harness.Source.GetCredentialAsync(CancellationToken.None);
		later!.Token.Should().Be(Access2);
		VerifyRefreshes(harness, Times.Exactly(2));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(5000)]
	[InlineData(9999)]
	public async Task Renewal_WithinTenSecondsOfYnabBeingUnavailable_FailsFastWithoutCallingYnab(int millisecondsLater)
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		YnabOAuthConnectionEntity? before = await harness.Repository.ReadRowAsync();
		SetupRefresh(harness, Refresh1, new YnabTokenResult(YnabTokenOutcome.Unavailable));
		Func<Task> act = async () => await harness.Source.GetCredentialAsync(CancellationToken.None);
		await act.Should().ThrowAsync<YnabTokenRenewalUnavailableException>();

		// Act
		harness.Time.Advance(TimeSpan.FromMilliseconds(millisecondsLater));
		Func<Task> again = async () => await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert — the same temporary failure, with only the first call having reached YNAB.
		YnabTokenRenewalUnavailableException exception =
			(await again.Should().ThrowAsync<YnabTokenRenewalUnavailableException>()).Which;
		exception.Should().BeAssignableTo<IYnabDefiniteRejection>();
		VerifyRefreshes(harness, Times.Once());
		(await harness.Repository.ReadRowAsync()).Should().BeEquivalentTo(before);
	}

	[Fact]
	public async Task Renewal_ManyCallersWhileYnabIsUnavailable_ReachYnabOnce()
	{
		// Arrange — without the backoff each queued caller would wait out its own timeout in turn.
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		SetupRefresh(harness, Refresh1, new YnabTokenResult(YnabTokenOutcome.Unavailable));

		// Act
		List<Exception?> failures = [];
		for (int i = 0; i < 10; i++)
		{
			failures.Add(await Record.ExceptionAsync(async () => await harness.Source.GetCredentialAsync(CancellationToken.None)));
		}

		// Assert
		failures.Should().AllBeOfType<YnabTokenRenewalUnavailableException>();
		VerifyRefreshes(harness, Times.Once());
	}

	[Fact]
	public async Task Renewal_AfterARejectedTokenWhileYnabIsUnavailable_AlsoFailsFast()
	{
		// Arrange — the 401 path shares the backoff with the expiry path.
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		YnabBearerCredential? rejected = await harness.Source.GetCredentialAsync(CancellationToken.None);
		SetupRefresh(harness, Refresh1, new YnabTokenResult(YnabTokenOutcome.Unavailable));
		Func<Task> act = async () => await harness.Source.RenewAfterRejectionAsync(rejected!, CancellationToken.None);
		await act.Should().ThrowAsync<YnabTokenRenewalUnavailableException>();

		// Act
		Func<Task> again = async () => await harness.Source.RenewAfterRejectionAsync(rejected!, CancellationToken.None);

		// Assert
		await again.Should().ThrowAsync<YnabTokenRenewalUnavailableException>();
		VerifyRefreshes(harness, Times.Once());
	}

	[Fact]
	public async Task Renewal_RefusedForClientSettings_DoesNotStartABackoff()
	{
		// Arrange — only "no answer" backs off; a refusal is an answer.
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		harness.OAuthClient
			.SetupSequence(c => c.RefreshAsync(Settings, Refresh1, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabTokenResult(YnabTokenOutcome.Rejected, Error: "invalid_client"))
			.ReturnsAsync(Success(Access2, Refresh2, Start.AddHours(2)));
		Func<Task> act = async () => await harness.Source.GetCredentialAsync(CancellationToken.None);
		await act.Should().ThrowAsync<YnabAuthException>();

		// Act
		YnabBearerCredential? again = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		again!.Token.Should().Be(Access2);
		VerifyRefreshes(harness, Times.Exactly(2));
	}

	[Fact]
	public async Task Renewal_BackoffIsClearedByANewConnection()
	{
		// Arrange — YNAB was unreachable for the old connection; an Admin then connects again,
		// with a token that itself needs renewing straight away.
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		SetupRefresh(harness, Refresh1, new YnabTokenResult(YnabTokenOutcome.Unavailable));
		Func<Task> act = async () => await harness.Source.GetCredentialAsync(CancellationToken.None);
		await act.Should().ThrowAsync<YnabTokenRenewalUnavailableException>();
		SetupRefresh(harness, Refresh2, Success(Access3, Refresh3, Start.AddHours(2)));

		// Act
		await harness.Source.StoreNewAsync(
			new YnabOAuthTokens(Access2, Refresh2, Start.AddMinutes(1)), "admin-1", CancellationToken.None);
		YnabBearerCredential? credential = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert — YNAB is asked at once; the old connection's backoff does not apply.
		credential!.Token.Should().Be(Access3);
		harness.OAuthClient.Verify(c => c.RefreshAsync(Settings, Refresh2, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Renewal_BackoffIsClearedByARemoval_EvenOneWhoseDeleteFailed()
	{
		// Arrange — after a successful removal there is no connection left to renew, so the cleared
		// backoff can only be seen when the delete fails and the row survives.
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		harness.OAuthClient
			.SetupSequence(c => c.RefreshAsync(Settings, Refresh1, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new YnabTokenResult(YnabTokenOutcome.Unavailable))
			.ReturnsAsync(Success(Access2, Refresh2, Start.AddHours(2)));
		Func<Task> act = async () => await harness.Source.GetCredentialAsync(CancellationToken.None);
		await act.Should().ThrowAsync<YnabTokenRenewalUnavailableException>();
		harness.Repository.BeforeDelete = () => throw new InvalidOperationException("database unavailable");
		Func<Task> removal = () => harness.Source.RemoveWithoutRevokingAsync();
		await removal.Should().ThrowAsync<InvalidOperationException>();

		// Act
		YnabBearerCredential? credential = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		credential!.Token.Should().Be(Access2);
		VerifyRefreshes(harness, Times.Exactly(2));
	}

	[Fact]
	public async Task Renewal_OAuthSettingsMissing_KeepsTheConnectionAndFailsAsUnauthenticated()
	{
		// Arrange — connected earlier, then the deployment lost its client settings.
		using YnabCredentialSourceHarness harness = new(pat: Pat, oauthSettings: false);
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		YnabOAuthConnectionEntity? before = await harness.Repository.ReadRowAsync();

		// Act
		Func<Task> act = async () => await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<YnabAuthException>();
		(await harness.Repository.ReadRowAsync()).Should().BeEquivalentTo(before);
		VerifyRefreshes(harness, Times.Never());
	}

	#endregion

	#region Unreadable stored tokens

	[Theory]
	[InlineData(Pat)]
	[InlineData(null)]
	public async Task StoredTokensFromAnotherKeyRing_BecomeReconnectRequiredWithoutThrowing(string? pat)
	{
		// Arrange — the row was written on a host with different Data Protection keys.
		using YnabCredentialSourceHarness harness = new(pat);
		YnabOAuthTokenProtector otherHost = new(new EphemeralDataProtectionProvider());
		await harness.Repository.Inner.SaveConnectedAsync(
			otherHost.Protect(Access1, Refresh1), Start.AddHours(2), "admin-1", Start, CancellationToken.None);

		// Act
		YnabCredentialState state = default!;
		YnabBearerCredential? credential = new("unset", "unset");
		Func<Task> act = async () =>
		{
			state = await harness.Source.GetStateAsync(CancellationToken.None);
			credential = await harness.Source.GetCredentialAsync(CancellationToken.None);
		};

		// Assert
		await act.Should().NotThrowAsync();
		state.Kind.Should().Be(YnabCredentialKind.OAuthReconnectRequired);
		credential.Should().BeNull();
		YnabOAuthConnectionEntity? row = await harness.Repository.ReadRowAsync();
		row!.Status.Should().Be(YnabOAuthConnectionStatus.ReconnectRequired);
		row.ProtectedTokens.Should().BeNull();
		VerifyRefreshes(harness, Times.Never());
	}

	#endregion

	#region Removing the connection

	[Theory]
	[InlineData("connected", Refresh1)]
	[InlineData("none", null)]
	[InlineData("reconnect required", null)]
	[InlineData("unreadable", null)]
	public async Task RemoveAsync_GivesTheCallbackTheStoredRefreshTokenOnlyForAReadableConnection(
		string stored, string? expectedRefreshToken)
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new();
		switch (stored)
		{
			case "connected":
				await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
				break;
			case "reconnect required":
				await harness.SeedReconnectRequiredAsync();
				break;
			case "unreadable":
				await harness.Repository.Inner.SaveConnectedAsync(
					new YnabOAuthTokenProtector(new EphemeralDataProtectionProvider()).Protect(Access1, Refresh1),
					Start.AddHours(2), "admin-1", Start, CancellationToken.None);
				break;
		}

		string? received = "unset";

		// Act
		string result = await harness.Source.RemoveAsync(
			(refreshToken, _) =>
			{
				received = refreshToken;
				return Task.FromResult("callback result");
			},
			CancellationToken.None);

		// Assert
		received.Should().Be(expectedRefreshToken);
		result.Should().Be("callback result");
		(await harness.Repository.ReadRowAsync()).Should().BeNull();
		(await harness.Source.GetStateAsync(CancellationToken.None)).Kind.Should().Be(YnabCredentialKind.None);
	}

	[Fact]
	public async Task RemoveAsync_AfterARenewal_GivesTheCallbackTheCurrentRefreshTokenNotTheRetiredOne()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		SetupRefresh(harness, Refresh1, Success(Access2, Refresh2, Start.AddHours(2)));
		await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Act
		string? received = await harness.Source.RemoveAsync(
			(refreshToken, _) => Task.FromResult(refreshToken), CancellationToken.None);

		// Assert
		received.Should().Be(Refresh2);
	}

	[Fact]
	public async Task RemoveAsync_RowStillThereWhileTheCallbackRuns_AndGoneAfterwards()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));

		// Act
		YnabOAuthConnectionEntity? duringCallback = await harness.Source.RemoveAsync(
			(_, _) => harness.Repository.ReadRowAsync(), CancellationToken.None);

		// Assert — the remote step runs first, so a crash during it leaves a connection to retry with.
		duringCallback.Should().NotBeNull();
		(await harness.Repository.ReadRowAsync()).Should().BeNull();
	}

	[Fact]
	public async Task RemoveAsync_CallbackThrows_StillDeletesTheRowAndPropagates()
	{
		// Arrange — the OAuth token is held in memory.
		using YnabCredentialSourceHarness harness = new(Pat);
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		(await harness.Source.GetCredentialAsync(CancellationToken.None))!.Token.Should().Be(Access1);

		// Act
		Func<Task> act = () => harness.Source.RemoveAsync<bool>(
			(_, _) => throw new InvalidOperationException("revocation blew up"), CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("revocation blew up");
		(await harness.Repository.ReadRowAsync()).Should().BeNull();
		(await harness.Source.GetStateAsync(CancellationToken.None)).Kind.Should().Be(YnabCredentialKind.PersonalAccessToken);
		(await harness.Source.GetCredentialAsync(CancellationToken.None))!.Token.Should().Be(Pat);
	}

	[Fact]
	public async Task RemoveAsync_RenewalStartedWhileTheCallbackRuns_CannotRotateTheTokenBeforeTheDelete()
	{
		// Arrange — the token was fresh when first read and has since expired, so the next caller
		// goes for a renewal. The removal's callback (the revocation at YNAB) blocks until released.
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		await harness.Source.GetCredentialAsync(CancellationToken.None);
		harness.Time.Advance(TimeSpan.FromHours(2));

		TaskCompletionSource<string?> callbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task<string> removal = harness.Source.RemoveAsync(
			async (refreshToken, _) =>
			{
				callbackEntered.SetResult(refreshToken);
				await releaseCallback.Task;
				return "revoked";
			},
			CancellationToken.None);
		string? tokenBeingRevoked = await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

		// Act
		Task<YnabBearerCredential?> credential = harness.Source.GetCredentialAsync(CancellationToken.None).AsTask();
		Task completedEarly = await Task.WhenAny(credential, Task.Delay(TimeSpan.FromMilliseconds(200)));

		// Assert — while the callback runs: no renewal, and the stored pair is the one being revoked.
		completedEarly.Should().NotBeSameAs(credential, "the renewal must wait for the removal");
		VerifyRefreshes(harness, Times.Never());
		tokenBeingRevoked.Should().Be(Refresh1);
		(await harness.ReadStoredTokensAsync()).Should().Be((Access1, Refresh1));

		releaseCallback.SetResult();
		(await removal.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("revoked");
		(await credential.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeNull("there is no connection left to renew");
		VerifyRefreshes(harness, Times.Never());
		(await harness.Repository.ReadRowAsync()).Should().BeNull();
		(await harness.Source.GetStateAsync(CancellationToken.None)).Kind.Should().Be(YnabCredentialKind.None);
	}

	#endregion

	#region Connect and disconnect

	[Fact]
	public async Task StoreNewAsync_WhenNothingWasConnected_SwitchesFromThePatToTheNewConnection()
	{
		// Arrange — the "no connection" answer is already held in memory.
		using YnabCredentialSourceHarness harness = new(Pat);
		(await harness.Source.GetCredentialAsync(CancellationToken.None))!.Token.Should().Be(Pat);
		harness.Time.Advance(TimeSpan.FromMinutes(3));
		DateTimeOffset connectedAt = harness.Time.GetUtcNow();

		// Act
		await harness.Source.StoreNewAsync(
			new YnabOAuthTokens(Access1, Refresh1, connectedAt.AddHours(2)), "admin-7", CancellationToken.None);

		// Assert
		YnabCredentialState state = await harness.Source.GetStateAsync(CancellationToken.None);
		state.Kind.Should().Be(YnabCredentialKind.OAuth);
		state.OAuthConnectedAtUtc.Should().Be(connectedAt);
		(await harness.Source.GetCredentialAsync(CancellationToken.None))!.Token.Should().Be(Access1);

		YnabOAuthConnectionEntity? row = await harness.Repository.ReadRowAsync();
		row!.Status.Should().Be(YnabOAuthConnectionStatus.Connected);
		row.ConnectedByUserId.Should().Be("admin-7");
		row.ConnectedAtUtc.Should().Be(connectedAt);
		row.AccessTokenExpiresAtUtc.Should().Be(connectedAt.AddHours(2));
		row.ProtectedTokens.Should().NotContain(Access1).And.NotContain(Refresh1);
		(await harness.ReadStoredTokensAsync()).Should().Be((Access1, Refresh1));
	}

	[Fact]
	public async Task StoreNewAsync_OverAnExistingConnection_StopsServingTheEarlierTokenAndChangesCacheIdentity()
	{
		// Arrange — the earlier connection's token is held in memory.
		using YnabCredentialSourceHarness harness = new();
		YnabOAuthConnectionEntity firstRow = await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		YnabBearerCredential? first = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Act
		await harness.Source.StoreNewAsync(
			new YnabOAuthTokens(Access2, Refresh2, Start.AddHours(2)), "admin-1", CancellationToken.None);
		YnabBearerCredential? second = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		YnabOAuthConnectionEntity? secondRow = await harness.Repository.ReadRowAsync();
		first!.Token.Should().Be(Access1);
		first.CacheIdentity.Should().Be($"oauth-{firstRow.ConnectionId:N}");
		second!.Token.Should().Be(Access2);
		second.CacheIdentity.Should().Be($"oauth-{secondRow!.ConnectionId:N}");
		second.CacheIdentity.Should().NotBe(first.CacheIdentity);
	}

	[Fact]
	public async Task StoreNewAsync_WhileReconnectIsRequired_RestoresTheOAuthConnection()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new(Pat);
		await harness.SeedReconnectRequiredAsync();
		(await harness.Source.GetStateAsync(CancellationToken.None)).Kind.Should().Be(YnabCredentialKind.OAuthReconnectRequired);

		// Act
		await harness.Source.StoreNewAsync(
			new YnabOAuthTokens(Access2, Refresh2, Start.AddHours(2)), "admin-1", CancellationToken.None);

		// Assert
		(await harness.Source.GetStateAsync(CancellationToken.None)).Kind.Should().Be(YnabCredentialKind.OAuth);
		(await harness.Source.GetCredentialAsync(CancellationToken.None))!.Token.Should().Be(Access2);
	}

	[Fact]
	public async Task RemoveAsync_ConnectedWithPat_DeletesTheRowAndFallsBackToThePat()
	{
		// Arrange — the OAuth token is held in memory.
		using YnabCredentialSourceHarness harness = new(Pat);
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		(await harness.Source.GetCredentialAsync(CancellationToken.None))!.Token.Should().Be(Access1);

		// Act
		await harness.Source.RemoveWithoutRevokingAsync();

		// Assert
		(await harness.Repository.ReadRowAsync()).Should().BeNull();
		YnabCredentialState state = await harness.Source.GetStateAsync(CancellationToken.None);
		state.Kind.Should().Be(YnabCredentialKind.PersonalAccessToken);
		state.OAuthConnectedAtUtc.Should().BeNull();
		(await harness.Source.GetCredentialAsync(CancellationToken.None))!.Token.Should().Be(Pat);
	}

	[Fact]
	public async Task RemoveAsync_ConnectedWithoutPat_LeavesNoCredential()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new(pat: null);
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Act
		await harness.Source.RemoveWithoutRevokingAsync();

		// Assert
		(await harness.Source.GetStateAsync(CancellationToken.None)).Kind.Should().Be(YnabCredentialKind.None);
		(await harness.Source.GetCredentialAsync(CancellationToken.None)).Should().BeNull();
	}

	[Fact]
	public async Task RemoveAsync_WhileReconnectIsRequired_ClearsItAndRestoresThePatFallback()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new(Pat);
		await harness.SeedReconnectRequiredAsync();
		(await harness.Source.GetCredentialAsync(CancellationToken.None)).Should().BeNull();

		// Act
		await harness.Source.RemoveWithoutRevokingAsync();

		// Assert
		(await harness.Repository.ReadRowAsync()).Should().BeNull();
		(await harness.Source.GetStateAsync(CancellationToken.None)).Kind.Should().Be(YnabCredentialKind.PersonalAccessToken);
		(await harness.Source.GetCredentialAsync(CancellationToken.None))!.Token.Should().Be(Pat);
	}

	[Fact]
	public async Task RemoveAsync_DeleteFails_DoesNotKeepServingTheTokenFromMemory()
	{
		// Arrange — the delete fails after the row is, in fact, gone (the answer was lost).
		using YnabCredentialSourceHarness harness = new(pat: null);
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		await harness.Source.GetCredentialAsync(CancellationToken.None);
		harness.Repository.AfterDelete = () => throw new InvalidOperationException("connection to the database was lost");

		// Act
		Func<Task> act = () => harness.Source.RemoveWithoutRevokingAsync();

		// Assert — what is true is unknown, so the next call reads the row instead of trusting memory.
		await act.Should().ThrowAsync<InvalidOperationException>();
		(await harness.Source.GetCredentialAsync(CancellationToken.None)).Should().BeNull();
	}

	[Fact]
	public async Task RemoveAsync_DeleteFailsAndTheRowSurvives_KeepsReportingTheConnection()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new(Pat);
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		harness.Repository.BeforeDelete = () => throw new InvalidOperationException("database unavailable");

		// Act
		Func<Task> act = () => harness.Source.RemoveWithoutRevokingAsync();

		// Assert — no silent switch to the PAT while a connection is still stored.
		await act.Should().ThrowAsync<InvalidOperationException>();
		(await harness.Source.GetStateAsync(CancellationToken.None)).Kind.Should().Be(YnabCredentialKind.OAuth);
		(await harness.Source.GetCredentialAsync(CancellationToken.None))!.Token.Should().Be(Access1);
	}

	#endregion

	#region Cache identity

	[Fact]
	public async Task OAuthCredential_CacheIdentity_NamesTheConnectionAndNeverTheToken()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new(Pat);
		YnabOAuthConnectionEntity row = await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));

		// Act
		YnabBearerCredential? credential = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert
		credential!.CacheIdentity.Should().StartWith("oauth-").And.Be($"oauth-{row.ConnectionId:N}");
		credential.CacheIdentity.Should().NotContain(Access1);
	}

	[Fact]
	public async Task OAuthCredential_CacheIdentity_SurvivesARenewalOfTheSameConnection()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new();
		YnabOAuthConnectionEntity row = await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddMinutes(1));
		SetupRefresh(harness, Refresh1, Success(Access2, Refresh2, Start.AddHours(2)));

		// Act
		YnabBearerCredential? renewed = await harness.Source.GetCredentialAsync(CancellationToken.None);

		// Assert — same YNAB account, so data cached under it is still valid.
		renewed!.Token.Should().Be(Access2);
		renewed.CacheIdentity.Should().Be($"oauth-{row.ConnectionId:N}");
	}

	#endregion

	#region Renewal after YNAB rejected a token

	[Fact]
	public async Task RenewAfterRejectionAsync_PatCredential_ReturnsNullWithoutTouchingTheConnection()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new(Pat);
		YnabBearerCredential? pat = await harness.Source.GetCredentialAsync(CancellationToken.None);
		int readsBefore = harness.Repository.GetCalls;

		// Act
		YnabBearerCredential? renewed = await harness.Source.RenewAfterRejectionAsync(pat!, CancellationToken.None);

		// Assert
		renewed.Should().BeNull();
		harness.Repository.GetCalls.Should().Be(readsBefore);
		VerifyRefreshes(harness, Times.Never());
	}

	[Fact]
	public async Task RenewAfterRejectionAsync_RejectedTokenIsTheStoredOne_RefreshesEvenThoughUnexpired()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		YnabBearerCredential? rejected = await harness.Source.GetCredentialAsync(CancellationToken.None);
		SetupRefresh(harness, Refresh1, Success(Access2, Refresh2, Start.AddHours(2)));

		// Act
		YnabBearerCredential? renewed = await harness.Source.RenewAfterRejectionAsync(rejected!, CancellationToken.None);

		// Assert
		renewed!.Token.Should().Be(Access2);
		renewed.CacheIdentity.Should().Be(rejected!.CacheIdentity);
		(await harness.ReadStoredTokensAsync()).Should().Be((Access2, Refresh2));
		(await harness.Source.GetCredentialAsync(CancellationToken.None))!.Token.Should().Be(Access2);
		VerifyRefreshes(harness, Times.Once());
	}

	[Fact]
	public async Task RenewAfterRejectionAsync_AnotherCallerAlreadyReplacedTheToken_ReturnsTheStoredOneWithoutRefreshing()
	{
		// Arrange — two requests were sent with the first token; the other one's 401 was handled first.
		using YnabCredentialSourceHarness harness = new();
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		YnabBearerCredential? rejected = await harness.Source.GetCredentialAsync(CancellationToken.None);
		SetupRefresh(harness, Refresh1, Success(Access2, Refresh2, Start.AddHours(2)));
		await harness.Source.RenewAfterRejectionAsync(rejected!, CancellationToken.None);

		// Act
		YnabBearerCredential? renewed = await harness.Source.RenewAfterRejectionAsync(rejected!, CancellationToken.None);

		// Assert
		renewed!.Token.Should().Be(Access2);
		VerifyRefreshes(harness, Times.Once());
	}

	[Fact]
	public async Task RenewAfterRejectionAsync_RefreshRefused_MovesToReconnectRequired()
	{
		// Arrange
		using YnabCredentialSourceHarness harness = new(Pat);
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		YnabBearerCredential? rejected = await harness.Source.GetCredentialAsync(CancellationToken.None);
		SetupRefresh(harness, Refresh1, new YnabTokenResult(YnabTokenOutcome.InvalidGrant, Error: "invalid_grant"));

		// Act
		Func<Task> act = async () => await harness.Source.RenewAfterRejectionAsync(rejected!, CancellationToken.None);

		// Assert
		await act.Should().ThrowAsync<YnabAuthException>();
		YnabOAuthConnectionEntity? row = await harness.Repository.ReadRowAsync();
		row!.Status.Should().Be(YnabOAuthConnectionStatus.ReconnectRequired);
		row.ProtectedTokens.Should().BeNull();
		(await harness.Source.GetStateAsync(CancellationToken.None)).Kind.Should().Be(YnabCredentialKind.OAuthReconnectRequired);
		(await harness.Source.GetCredentialAsync(CancellationToken.None)).Should().BeNull();
	}

	[Fact]
	public async Task RenewAfterRejectionAsync_ConnectionWasRemovedMeanwhile_FallsBackToThePat()
	{
		// Arrange — an Admin disconnected between the request being sent and its 401 arriving.
		using YnabCredentialSourceHarness harness = new(Pat);
		await harness.SeedConnectedAsync(Access1, Refresh1, Start.AddHours(2));
		YnabBearerCredential? rejected = await harness.Source.GetCredentialAsync(CancellationToken.None);
		await harness.Source.RemoveWithoutRevokingAsync();

		// Act
		YnabBearerCredential? renewed = await harness.Source.RenewAfterRejectionAsync(rejected!, CancellationToken.None);

		// Assert
		renewed!.Token.Should().Be(Pat);
		VerifyRefreshes(harness, Times.Never());
	}

	#endregion
}
