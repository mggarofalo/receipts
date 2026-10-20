namespace Infrastructure.NormalizationAi;

public sealed class NormalizationAiOptions
{
	public int PendingRunLimit { get; set; } = 10;
	public int UnacceptedRetentionDays { get; set; } = 30;
	public bool Enabled { get; set; }
	public string OwnerUserId { get; set; } = string.Empty;
	public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
}
