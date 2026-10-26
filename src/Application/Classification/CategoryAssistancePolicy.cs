using Application.Taxonomy;

namespace Application.Classification;

/// <summary>Deployment-wide consent for a server API-key service; no personal integration owner.</summary>
public sealed record CategoryAssistanceSettings(bool Enabled, string? Provider, string? Model,
	int DailyLimit, int PendingLimit, Guid Revision)
{
	public static CategoryAssistanceSettings Default => new(false, null, null, 10, 3, Guid.NewGuid());
}

/// <summary>Unknown capability fails closed. Connection status does not establish this capability.</summary>
public enum CategoryAssistanceCapabilityState { Unknown, Disabled, Unavailable, Ready }
public sealed record CategoryAssistanceCapability(string Provider, CategoryAssistanceCapabilityState State,
	IReadOnlyList<string> Models);

public enum CategoryAssistanceRoute { PreserveManual, UseApprovedMapping, PreserveExisting, Skip, Suggest }
public enum CategoryAssistanceClassificationState { Unknown, Unassigned, UnresolvedObservation, ManualChoice, ProtectedAssignment }

/// <summary>Eligibility includes authoritative no-choice/exclusion evidence, not merely a null category.</summary>
public sealed record CategoryAssistanceEligibility(CategoryAssistanceClassificationState Classification,
	bool HasValidApprovedExactMapping, bool IsPersisted, bool IsLive, bool IsEligibleUnknown);

/// <summary>Persisted snapshots compared before dispatch, result persistence and explicit acceptance.</summary>
public sealed record CategoryAssistanceFence(Guid SettingsRevision, Guid ConnectionGeneration,
	Guid RestoreGeneration, string SourceHash, Guid SourceRevision, Guid TaxonomyRevision, Guid? AssignmentId);

/// <summary>One deployment ledger per UTC day, independent of actors, providers and settings revisions.</summary>
public sealed record CategoryAssistanceDailyUsage(DateOnly UtcDay, int DispatchAttempts);

/// <summary>Pure policy only. Suggest never assigns a category or approves a mapping.</summary>
public static class CategoryAssistancePolicy
{
	public static bool IsValid(CategoryAssistanceSettings settings) =>
		settings.Revision != Guid.Empty && settings.DailyLimit is >= 1 and <= 100 && settings.PendingLimit is >= 1 and <= 20
		&& (!settings.Enabled || (!string.IsNullOrWhiteSpace(settings.Provider) && !string.IsNullOrWhiteSpace(settings.Model)));

	/// <summary>Any currently authorized active Admin may update; persistence must audit actor and atomically compare revision.</summary>
	public static bool CanUpdateSettings(CategoryAssistanceSettings current, Guid expectedRevision,
		string actorId, bool activeAdminAuthorized) => activeAdminAuthorized && !string.IsNullOrWhiteSpace(actorId)
		&& expectedRevision != Guid.Empty && current.Revision == expectedRevision;

	/// <summary>Global readiness and consent. Per-action authorization is checked separately, never against the enabling Admin.</summary>
	public static bool CanSuggest(CategoryAssistanceSettings settings, CategoryAssistanceCapability capability) =>
		IsValid(settings) && settings.Enabled && capability.State == CategoryAssistanceCapabilityState.Ready
		&& string.Equals(settings.Provider, capability.Provider, StringComparison.Ordinal)
		&& capability.Models.Contains(settings.Model, StringComparer.Ordinal);

	/// <summary>Record eligible saves under consent even when the provider is temporarily unavailable.</summary>
	public static bool CanEnroll(bool authenticatedReceiptWriterAuthorized, bool isPostConsentSave, CategoryAssistanceSettings settings) =>
		authenticatedReceiptWriterAuthorized && isPostConsentSave && IsValid(settings) && settings.Enabled;

	/// <summary>Rows created by legacy observation are not deliberate clears. Eligibility still requires save/exclusion evidence.</summary>
	public static CategoryAssistanceClassificationState ClassificationState(PurchaseClassification? current) => current switch
	{
		{ Source: "manual" } => CategoryAssistanceClassificationState.ManualChoice,
		{ CategoryId: not null } => CategoryAssistanceClassificationState.ProtectedAssignment,
		{ Source: "legacy_write", LegacyResolution: "unresolved_path" } => CategoryAssistanceClassificationState.UnresolvedObservation,
		{ Source: "legacy_import" } => CategoryAssistanceClassificationState.UnresolvedObservation,
		null => CategoryAssistanceClassificationState.Unassigned,
		_ => CategoryAssistanceClassificationState.Unknown
	};

	public static CategoryAssistanceRoute Route(CategoryAssistanceEligibility facts, bool canSuggest)
	{
		if (facts.Classification == CategoryAssistanceClassificationState.ManualChoice) { return CategoryAssistanceRoute.PreserveManual; }
		if (facts.Classification == CategoryAssistanceClassificationState.ProtectedAssignment) { return CategoryAssistanceRoute.PreserveExisting; }
		if (facts.Classification is not (CategoryAssistanceClassificationState.Unassigned or CategoryAssistanceClassificationState.UnresolvedObservation)
			|| !facts.IsLive || !facts.IsEligibleUnknown) { return CategoryAssistanceRoute.Skip; }
		if (facts.HasValidApprovedExactMapping) { return CategoryAssistanceRoute.UseApprovedMapping; }
		return facts.IsPersisted && canSuggest ? CategoryAssistanceRoute.Suggest : CategoryAssistanceRoute.Skip;
	}

	/// <summary>Admission/dispatch must reserve atomically; consumed usage is never rewritten when settings change.</summary>
	public static bool CanReserveDailyAttempt(CategoryAssistanceSettings settings, CategoryAssistanceDailyUsage usage, DateTimeOffset now)
	{
		DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
		return IsValid(settings) && usage.DispatchAttempts >= 0 && usage.UtcDay <= today
			&& (usage.UtcDay < today || usage.DispatchAttempts < settings.DailyLimit);
	}

	public static bool IsCurrent(CategoryAssistanceFence captured, CategoryAssistanceFence current,
		bool actionAuthorized, bool canSuggest, bool sourceStillEligible) =>
		actionAuthorized && canSuggest && sourceStillEligible && captured == current;
}
