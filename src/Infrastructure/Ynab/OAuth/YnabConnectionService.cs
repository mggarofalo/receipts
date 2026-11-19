using Application.Interfaces.Services;
using Application.Models.Ynab;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Ynab.OAuth;

public class YnabConnectionService(
	YnabOAuthSettingsProvider settingsProvider,
	YnabAuthorizationAttemptStore attempts,
	IYnabOAuthClient oauthClient,
	IYnabOAuthConnectionStore connectionStore,
	ILogger<YnabConnectionService> logger) : IYnabConnectionService
{
	public Task<string?> StartAuthorizationAsync(string userId, CancellationToken cancellationToken)
	{
		YnabOAuthSettings? settings = settingsProvider.Current;
		if (settings is null)
		{
			return Task.FromResult<string?>(null);
		}

		YnabAuthorizationAttempt attempt = attempts.Create(userId);
		return Task.FromResult<string?>(oauthClient.BuildAuthorizationUrl(settings, attempt.State, attempt.CodeChallenge));
	}

	public async Task<YnabAuthorizationCompletion> CompleteAuthorizationAsync(
		string userId, string code, string state, CancellationToken cancellationToken)
	{
		// Checked before the attempt is taken, so a deployment whose settings went missing does
		// not burn the attempt.
		YnabOAuthSettings? settings = settingsProvider.Current;
		if (settings is null)
		{
			return YnabAuthorizationCompletion.AttemptNotValid;
		}

		YnabAuthorizationAttempt? attempt = attempts.Take(state, userId);
		if (attempt is null)
		{
			return YnabAuthorizationCompletion.AttemptNotValid;
		}

		YnabTokenResult result;
		try
		{
			result = await oauthClient.ExchangeCodeAsync(settings, code, attempt.CodeVerifier, cancellationToken);
		}
		catch
		{
			attempts.Restore(attempt);
			throw;
		}

		switch (result.Outcome)
		{
			case YnabTokenOutcome.Success:
				try
				{
					await connectionStore.StoreNewAsync(result.Tokens!, userId, cancellationToken);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					// YNAB has consumed the code, so this attempt is spent; the Admin starts again.
					logger.LogError(ex, "YNAB authorized the connection but it could not be stored");
					throw;
				}

				logger.LogInformation("YNAB was connected with OAuth by user {UserId}", userId);
				return YnabAuthorizationCompletion.Connected;

			case YnabTokenOutcome.Unavailable:
				// YNAB does not consume a code on a request it never answered.
				attempts.Restore(attempt);
				return YnabAuthorizationCompletion.YnabUnavailable;

			default:
				logger.LogWarning("YNAB rejected an authorization code (error {Error})", result.Error ?? "(none)");
				return YnabAuthorizationCompletion.RejectedByYnab;
		}
	}

	public async Task<YnabRemoteRevocation> DisconnectAsync(CancellationToken cancellationToken)
	{
		// The stored tokens are removed whatever happens in here; a failed remote step must not
		// keep them.
		YnabRemoteRevocation revocation = await connectionStore.RemoveAsync(
			async (refreshToken, ct) =>
			{
				YnabOAuthSettings? settings = settingsProvider.Current;
				if (refreshToken is null || settings is null)
				{
					return YnabRemoteRevocation.NotAttempted;
				}

				try
				{
					// Revoking either token ends the whole grant at YNAB.
					return await oauthClient.RevokeAsync(settings, refreshToken, ct)
						? YnabRemoteRevocation.Succeeded
						: YnabRemoteRevocation.Failed;
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					logger.LogWarning(ex, "Revoking the YNAB connection failed; removing the stored tokens anyway");
					return YnabRemoteRevocation.Failed;
				}
			},
			cancellationToken);

		logger.LogInformation("The YNAB OAuth connection was removed (remote revocation: {Revocation})", revocation);
		return revocation;
	}
}
