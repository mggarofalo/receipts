namespace Application.Normalization;

/// <summary>Outstanding purchase review and optional assistance; neither grants verification.</summary>
public interface INormalizationReviewInboxService
{
	Task<NormalizationReviewInboxPage> ListAsync(string actor, string? query, int offset, int limit, CancellationToken cancellationToken);
	Task<NormalizationAssistancePreferences> GetPreferencesAsync(string actor, CancellationToken cancellationToken);
	Task<NormalizationAssistancePreferences> UpdatePreferencesAsync(string actor, NormalizationAssistancePreferenceUpdate update, CancellationToken cancellationToken);
	Task RetryAdmissionAsync(string actor, string sourceHash, Guid expectedRevision, CancellationToken cancellationToken);
}

public sealed record NormalizationReviewReceipt(Guid ReceiptId, Guid ItemId, DateOnly Date);
public sealed record NormalizationReviewInboxSource(string SourceHash, NormalizationSourceKey Source, int PurchaseCount,
	int ReceiptCount, DateTimeOffset FirstSeen, IReadOnlyList<NormalizationReviewReceipt> Receipts,
	string AssistanceState, Guid? AttemptRevision, Guid? RunId, string? ErrorCode, string? ErrorDetail);
public sealed record NormalizationReviewInboxPage(IReadOnlyList<NormalizationReviewInboxSource> Items, int Total, int Offset, int Limit);
public sealed record NormalizationAssistancePreferences(bool IsOwner, bool Enabled, string? Model, int DailyLimit, Guid Revision);
public sealed record NormalizationAssistancePreferenceUpdate(bool Enabled, string? Model, int DailyLimit, Guid ExpectedRevision);

/// <summary>Bounded admission retries happen before a durable provider run exists.</summary>
public static class NormalizationAssistancePolicy
{
	public const int MaximumAdmissionAttempts = 5;
	public static TimeSpan AdmissionBackoff(int attempts) => TimeSpan.FromSeconds(Math.Min(1800, 30 * Math.Pow(2, Math.Max(0, attempts - 1))));
	public static void ValidatePreferences(NormalizationAssistancePreferenceUpdate update)
	{
		if (update.DailyLimit is < 1 or > 100 || update.Model?.Length > 200 || (update.Enabled && string.IsNullOrWhiteSpace(update.Model)))
		{
			throw new NormalizationCatalogException(400, "invalid_assistance_preferences", "Choose a model and a daily limit between 1 and 100 source groups.");
		}
	}
}
