namespace Application.Models.Ynab;

/// <param name="Credential">Which credential is in use and whether OAuth can be started here.</param>
public record YnabConnectionStatus(
	bool IsConfigured,
	bool IsConnected,
	DateTimeOffset? LastSuccessfulSyncUtc,
	YnabCredentialState Credential);
