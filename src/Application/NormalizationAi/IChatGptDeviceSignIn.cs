namespace Application.NormalizationAi;

/// <summary>Owner-scoped sign-in ceremony. HTTP callers must additionally require the admin role.</summary>
public interface IChatGptDeviceSignIn
{
	ChatGptSignInAttempt Start(string actor, bool ownerConsent);
	ChatGptSignInAttempt Get(string actor, Guid attemptId);
	Task<ChatGptSignInAttempt> CancelAsync(string actor, Guid attemptId, CancellationToken cancellationToken);
	Task DisconnectAsync(string actor, CancellationToken cancellationToken);
}

/// <summary>Contains only browser-safe ceremony data; never OAuth credentials or provider diagnostics.</summary>
public sealed record ChatGptSignInAttempt(Guid Id, string State, string? VerificationUrl = null,
	string? UserCode = null, DateTimeOffset? ExpiresAt = null);
