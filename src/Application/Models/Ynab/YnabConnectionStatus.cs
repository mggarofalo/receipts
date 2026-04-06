namespace Application.Models.Ynab;

public record YnabConnectionStatus(bool IsConfigured, bool IsConnected, DateTimeOffset? LastSuccessfulSyncUtc);
