using Application.Classification;
using Application.Taxonomy;

namespace Application.Tests.Classification;

public class CategoryAssistancePolicyTests
{
	private static readonly CategoryAssistanceSettings Settings = new(true, "provider", "model", 10, 3, Guid.NewGuid());
	private static readonly CategoryAssistanceCapability Capability = new("provider", CategoryAssistanceCapabilityState.Ready, ["model"]);
	private static readonly CategoryAssistanceEligibility Unknown = new(CategoryAssistanceClassificationState.Unassigned, false, true, true, true);

	[Fact]
	public void DefaultsRequireExplicitGlobalConsentEvenWithReadyProvider()
	{
		Assert.False(CategoryAssistanceSettings.Default.Enabled);
		Assert.False(CategoryAssistancePolicy.CanSuggest(CategoryAssistanceSettings.Default, Capability));
		Assert.True(CategoryAssistancePolicy.CanSuggest(Settings, Capability));
	}

	[Fact]
	public void AnyCurrentlyAuthorizedActiveAdminCanManageSettingsWithAuditableActorAndCas()
	{
		foreach (string actor in new[] { "enabling-admin", "another-admin" })
		{
			Assert.True(CategoryAssistancePolicy.CanUpdateSettings(Settings, Settings.Revision, actor, true));
			Assert.False(CategoryAssistancePolicy.CanUpdateSettings(Settings, Settings.Revision, actor, false));
			Assert.False(CategoryAssistancePolicy.CanUpdateSettings(Settings, Guid.NewGuid(), actor, true));
		}
		Assert.False(CategoryAssistancePolicy.CanUpdateSettings(Settings, Settings.Revision, "", true));
		// An enabling Admin's later role change is not an input to deployment consent/readiness.
		Assert.True(CategoryAssistancePolicy.CanSuggest(Settings, Capability));
	}

	[Theory]
	[InlineData(true, true, true, true)]
	[InlineData(false, true, true, false)]
	[InlineData(true, false, true, false)]
	[InlineData(true, true, false, false)]
	public void ReceiptWritersEnrollOnlyAuthorizedPostConsentSaves(bool writer, bool postConsent, bool consent, bool expected) =>
		Assert.Equal(expected, CategoryAssistancePolicy.CanEnroll(writer, postConsent, Settings with { Enabled = consent }));

	[Fact]
	public void GlobalConsentCanEnrollSaveWhileProviderReadinessBlocksDispatch()
	{
		Assert.True(CategoryAssistancePolicy.CanEnroll(true, true, Settings));
		Assert.False(CategoryAssistancePolicy.CanSuggest(Settings, Capability with { State = CategoryAssistanceCapabilityState.Unavailable }));
	}

	[Theory]
	[InlineData(CategoryAssistanceCapabilityState.Unknown)]
	[InlineData(CategoryAssistanceCapabilityState.Disabled)]
	[InlineData(CategoryAssistanceCapabilityState.Unavailable)]
	public void CapabilityMustBeKnownReady(CategoryAssistanceCapabilityState state) =>
		Assert.False(CategoryAssistancePolicy.CanSuggest(Settings, Capability with { State = state }));

	[Fact]
	public void ConfiguredProviderAndModelMustMatchCapability()
	{
		Assert.False(CategoryAssistancePolicy.CanSuggest(Settings, Capability with { Provider = "other" }));
		Assert.False(CategoryAssistancePolicy.CanSuggest(Settings, Capability with { Models = ["other"] }));
		Assert.False(CategoryAssistancePolicy.CanSuggest(Settings with { Enabled = false }, Capability));
	}

	[Theory]
	[InlineData(0, 3)]
	[InlineData(101, 3)]
	[InlineData(10, 0)]
	[InlineData(10, 21)]
	public void InvalidLimitsDoNotAuthorizeInference(int daily, int pending) =>
		Assert.False(CategoryAssistancePolicy.CanSuggest(Settings with { DailyLimit = daily, PendingLimit = pending }, Capability));

	[Fact]
	public void ManualClearsAndProtectedAssignmentsWinAndMappingsNeedNoProvider()
	{
		CategoryAssistanceEligibility mapped = Unknown with { HasValidApprovedExactMapping = true };
		Assert.Equal(CategoryAssistanceRoute.PreserveManual, CategoryAssistancePolicy.Route(mapped with { Classification = CategoryAssistanceClassificationState.ManualChoice }, true));
		Assert.Equal(CategoryAssistanceRoute.PreserveExisting, CategoryAssistancePolicy.Route(mapped with { Classification = CategoryAssistanceClassificationState.ProtectedAssignment }, true));
		Assert.Equal(CategoryAssistanceRoute.UseApprovedMapping, CategoryAssistancePolicy.Route(mapped, false));
		Assert.Equal(CategoryAssistanceRoute.Skip, CategoryAssistancePolicy.Route(mapped with { IsLive = false }, true));
		Assert.Equal(CategoryAssistanceRoute.Skip, CategoryAssistancePolicy.Route(mapped with { IsEligibleUnknown = false }, true));
	}

	private static PurchaseClassification Assignment(string source, Guid? category, string? resolution = null) =>
		new(Guid.NewGuid(), Guid.NewGuid(), 1, null, category, Guid.NewGuid(), DateTimeOffset.UtcNow,
			"writer", source, "Observed or chosen", Guid.NewGuid(), [], LegacyResolution: resolution);

	[Fact]
	public void NullLegacyObservationIsNotAnExplicitClearAndCanQualifyAfterNoChoiceSave()
	{
		PurchaseClassification observed = Assignment("legacy_write", null, "unresolved_path");
		CategoryAssistanceEligibility eligible = Unknown with { Classification = CategoryAssistancePolicy.ClassificationState(observed) };
		Assert.Equal(CategoryAssistanceClassificationState.UnresolvedObservation, eligible.Classification);
		Assert.Equal(CategoryAssistanceRoute.Suggest, CategoryAssistancePolicy.Route(eligible, true));
		// Null legacy category alone does not prove enrollment: dirty/ambiguous labels or untouched history remain excluded.
		Assert.Equal(CategoryAssistanceRoute.Skip, CategoryAssistancePolicy.Route(eligible with { IsEligibleUnknown = false }, true));
		Assert.Equal(CategoryAssistanceClassificationState.ManualChoice, CategoryAssistancePolicy.ClassificationState(Assignment("manual", null)));
		Assert.Equal(CategoryAssistanceClassificationState.ProtectedAssignment, CategoryAssistancePolicy.ClassificationState(Assignment("legacy_write", Guid.NewGuid(), "resolved_path")));
		Assert.Equal(CategoryAssistanceClassificationState.UnresolvedObservation, CategoryAssistancePolicy.ClassificationState(Assignment("legacy_import", null)));
		Assert.Equal(CategoryAssistanceClassificationState.Unassigned, CategoryAssistancePolicy.ClassificationState(null));
		Assert.Equal(CategoryAssistanceRoute.Skip, CategoryAssistancePolicy.Route(Unknown with { Classification = CategoryAssistancePolicy.ClassificationState(Assignment("future-source", null)) }, true));
	}

	[Fact]
	public void InferenceIsOnlyForSavedLiveEligibleUnknowns()
	{
		Assert.Equal(CategoryAssistanceRoute.Suggest, CategoryAssistancePolicy.Route(Unknown, true));
		foreach (CategoryAssistanceEligibility facts in new[] { Unknown with { IsPersisted = false }, Unknown with { IsLive = false }, Unknown with { IsEligibleUnknown = false } })
		{
			Assert.Equal(CategoryAssistanceRoute.Skip, CategoryAssistancePolicy.Route(facts, true));
		}
		Assert.Equal(CategoryAssistanceRoute.Skip, CategoryAssistancePolicy.Route(Unknown, false));
	}

	[Fact]
	public void DailyAllowanceSurvivesAdminProviderAndSettingsChangesAndResetsOnlyOnUtcDay()
	{
		DateTimeOffset now = new(2026, 10, 26, 23, 30, 0, TimeSpan.Zero);
		CategoryAssistanceDailyUsage used = new(new(2026, 10, 26), 10);
		foreach (CategoryAssistanceSettings settings in new[] { Settings, Settings with { Provider = "other", Model = "other", Revision = Guid.NewGuid() }, Settings with { DailyLimit = 5 } })
		{
			Assert.False(CategoryAssistancePolicy.CanReserveDailyAttempt(settings, used, now));
			Assert.True(CategoryAssistancePolicy.CanReserveDailyAttempt(settings, used, now.AddHours(1)));
		}
		Assert.False(CategoryAssistancePolicy.CanReserveDailyAttempt(Settings, used, now.ToOffset(TimeSpan.FromHours(2))));
		Assert.True(CategoryAssistancePolicy.CanReserveDailyAttempt(Settings, used with { DispatchAttempts = 9 }, now));
		Assert.False(CategoryAssistancePolicy.CanReserveDailyAttempt(Settings, used with { DispatchAttempts = -1 }, now));
		Assert.False(CategoryAssistancePolicy.CanReserveDailyAttempt(Settings, used with { UtcDay = new(2026, 10, 27) }, now));
	}

	[Fact]
	public void EveryCapturedSourceAndGlobalAuthorityMustStillMatchAndActionMustBeAuthorized()
	{
		CategoryAssistanceFence captured = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "source", Guid.NewGuid(), Guid.NewGuid(), null);
		Assert.True(CategoryAssistancePolicy.IsCurrent(captured, captured, true, true, true));
		CategoryAssistanceFence[] stale = [captured with { SettingsRevision = Guid.NewGuid() },
			captured with { ConnectionGeneration = Guid.NewGuid() }, captured with { RestoreGeneration = Guid.NewGuid() },
			captured with { SourceHash = "changed" }, captured with { SourceRevision = Guid.NewGuid() },
			captured with { TaxonomyRevision = Guid.NewGuid() }, captured with { AssignmentId = Guid.NewGuid() }];
		foreach (CategoryAssistanceFence current in stale) { Assert.False(CategoryAssistancePolicy.IsCurrent(captured, current, true, true, true)); }
		Assert.False(CategoryAssistancePolicy.IsCurrent(captured, captured, false, true, true));
		Assert.False(CategoryAssistancePolicy.IsCurrent(captured, captured, true, false, true));
		Assert.False(CategoryAssistancePolicy.IsCurrent(captured, captured, true, true, false));
	}
}
