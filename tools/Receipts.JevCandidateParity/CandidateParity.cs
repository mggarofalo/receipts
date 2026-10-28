using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.Classification;
using Application.Classification.Experimental;

namespace Receipts.JevCandidateParity;

public sealed record CandidateParityFixture(string Id, CandidatePurchase Purchase, string TaxonomyRevision,
	IReadOnlyList<SuggestionCategory> Taxonomy, IReadOnlyList<string> ShortlistedIds, CandidateJudgment Judgment);

// An offline snapshot contract owned by this adapter; never a TypeSafe wire request.
public sealed record CandidatePlanSnapshot(string Format, string PolicyVersion, string EvidenceRule,
	double SupportedThreshold, double RejectedThreshold, CandidateNoulQuestion PurchaseEvidenceQuestion,
	CandidateNoulQuestion UnlistedAlternativeQuestion, CandidateNoulQuestion PathSupportQuestion,
	CandidateNoulQuestion PathContextQuestion, CandidateVerificationPlan Plan);

public sealed record CandidateParityRecord(string FixtureId, string PlanSha256, string DecisionSha256,
	CandidatePlanSnapshot Plan, CandidateDecision Decision);

/// <summary>Calls the experimental plan and reducer offline. No transport, provider, or activation.</summary>
public static class CandidateParity
{
	public const string SnapshotFormat = "candidate-offline-plan-v1";
	public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

	public static CandidateParityRecord Evaluate(CandidateParityFixture fixture)
	{
		ArgumentNullException.ThrowIfNull(fixture);
		if (string.IsNullOrWhiteSpace(fixture.Id) || fixture.Id.Length > 200)
		{
			throw new ArgumentException("A fixture id of at most 200 characters is required.", nameof(fixture));
		}

		CandidateVerificationPlan plan = SupportedCategoryCandidate.Plan(fixture.Purchase,
			fixture.TaxonomyRevision, fixture.Taxonomy, fixture.ShortlistedIds);
		CandidatePlanSnapshot snapshot = new(SnapshotFormat, SupportedCategoryCandidate.Version,
			SupportedCategoryCandidate.EvidenceRule, SupportedCategoryCandidate.SupportedThreshold,
			SupportedCategoryCandidate.RejectedThreshold, SupportedCategoryCandidate.PurchaseEvidenceQuestion,
			SupportedCategoryCandidate.UnlistedAlternativeQuestion, SupportedCategoryCandidate.PathSupportQuestion,
			SupportedCategoryCandidate.PathContextQuestion, plan);
		CandidateDecision decision = SupportedCategoryCandidate.Decide(plan, fixture.Judgment);
		return new(fixture.Id, Fingerprint(snapshot), Fingerprint(decision), snapshot, decision);
	}

	private static string Fingerprint<T>(T value) =>
		Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)));

	private static JsonSerializerOptions CreateJsonOptions()
	{
		JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
		{
			UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
			RespectRequiredConstructorParameters = true
		};
		options.MakeReadOnly(populateMissingResolver: true);
		return options;
	}
}
