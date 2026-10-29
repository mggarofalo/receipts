using System.Text.Json;
using System.Text.Json.Serialization;

namespace Application.Normalization;

/// <summary>Private sidecar authority for a portable-file transformation. Proposals have no Review.</summary>
public sealed record OfflineNormalizationManifest(
	int ManifestVersion, string SourceSha256, int SourceFormatVersion, string FactualBaselineSha256,
	string ToolRevision, string ProposedBy, DateTimeOffset ProposedAt,
	IReadOnlyList<OfflineCanonicalDecision> CanonicalDecisions,
	IReadOnlyList<OfflineAssignmentDecision> Assignments,
	IReadOnlyList<OfflineConsolidationDecision> Consolidations,
	IReadOnlyList<OfflineUnresolvedDecision> Unresolved,
	OfflineManifestReview? Review);

public sealed record OfflineCanonicalDecision(Guid DecisionId, Guid CanonicalId, string CanonicalName, string? DisplayLabel);
public sealed record OfflineAssignmentDecision(Guid DecisionId, string EntityKind, Guid EntityId,
	NormalizationSourceKey Source, string SourceHash, Guid? CanonicalId, bool Exclude, bool RememberSource, string? ExclusionReason);
public sealed record OfflineConsolidationDecision(Guid DecisionId, Guid OldCanonicalId, Guid SurvivorCanonicalId);
public sealed record OfflineUnresolvedDecision(string EntityKind, Guid EntityId, string Reason);
public sealed record OfflineReviewedDecision(Guid DecisionId, string Rationale);
public sealed record OfflineManifestReview(string ReviewerIdentity, DateTimeOffset ReviewedAt, IReadOnlyList<OfflineReviewedDecision> Decisions);

public static class OfflineNormalizationManifestPolicy
{
	public static JsonSerializerOptions JsonOptions { get; } = new()
	{
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		PropertyNameCaseInsensitive = false,
		RespectNullableAnnotations = true,
		RespectRequiredConstructorParameters = true,
		MaxDepth = 16,
		WriteIndented = true
	};

	public static OfflineNormalizationManifest Read(string json, bool requireReviewed, DateTimeOffset now)
	{
		using (JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }))
		{
			RejectDuplicateProperties(document.RootElement);
		}
		OfflineNormalizationManifest manifest = JsonSerializer.Deserialize<OfflineNormalizationManifest>(json, JsonOptions)
			?? throw new InvalidDataException("A manifest object is required.");
		Validate(manifest, requireReviewed, now);
		return manifest;
	}

	public static void Validate(OfflineNormalizationManifest manifest, bool requireReviewed, DateTimeOffset now)
	{
		Require(manifest.ManifestVersion == 1 && manifest.SourceFormatVersion is 6 or 8 or 9 or 10 or 11 or 12 or 13 or 14, "Unsupported manifest or source format.");
		Require(IsSha256(manifest.SourceSha256) && IsSha256(manifest.FactualBaselineSha256), "SHA-256 values must be uppercase hexadecimal.");
		Require(Text(manifest.ToolRevision, 200) && Text(manifest.ProposedBy, 300), "Tool revision and proposer identity are required.");
		Require(manifest.ProposedAt != default && manifest.ProposedAt <= now, "Proposal requires an actual timestamp.");
		Require(manifest.CanonicalDecisions is not null && manifest.Assignments is not null && manifest.Consolidations is not null && manifest.Unresolved is not null, "Decision collections are required.");
		HashSet<Guid> ids = [];
		foreach (OfflineCanonicalDecision decision in manifest.CanonicalDecisions!)
		{
			Require(decision is not null, "Null decisions are invalid.");
			Require(ids.Add(decision!.DecisionId) && decision.DecisionId != Guid.Empty && decision.CanonicalId != Guid.Empty, "Decision and canonical IDs must be nonempty and unique.");
			Require(Text(decision.CanonicalName, 200) && (decision.DisplayLabel is null || Text(decision.DisplayLabel, 200)), "Readable canonical names are required.");
		}
		Require(manifest.CanonicalDecisions!.Select(d => d.CanonicalId).Distinct().Count() == manifest.CanonicalDecisions.Count, "Canonical decisions cannot repeat a target.");
		HashSet<(string, Guid)> observations = [];
		foreach (OfflineAssignmentDecision decision in manifest.Assignments!)
		{
			Require(decision is not null, "Null decisions are invalid.");
			Require(ids.Add(decision!.DecisionId) && decision.DecisionId != Guid.Empty, "Decision IDs must be nonempty and unique.");
			Require(decision.EntityKind is "item" or "template" && decision.EntityId != Guid.Empty && observations.Add((decision.EntityKind, decision.EntityId)), "Assignment observations must be explicit and unique.");
			Require(decision.Source is not null && decision.Source.Location is not null && decision.Source.Description is not null, "An explicit source scope is required.");
			Require(decision.Source == NormalizationCatalogPolicy.Normalize(decision.Source!) && decision.SourceHash == NormalizationCatalogPolicy.Hash(decision.Source!), "Source must use the catalog's normalized key and fingerprint.");
			Require(decision.Exclude != decision.CanonicalId.HasValue && decision.CanonicalId != Guid.Empty, "Choose exactly one target or exclusion.");
			Require(!decision.Exclude || Text(decision.ExclusionReason, 2000), "Exclusion requires a reason.");
			Require(decision.Exclude || decision.ExclusionReason is null, "Assignments cannot carry an exclusion reason.");
			Require(decision.EntityKind == "item" || !decision.RememberSource, "Templates cannot authorize a purchase-source rule.");
		}
		HashSet<Guid> oldIds = [];
		foreach (OfflineConsolidationDecision decision in manifest.Consolidations!)
		{
			Require(decision is not null, "Null decisions are invalid.");
			Require(ids.Add(decision!.DecisionId) && decision.DecisionId != Guid.Empty && decision.OldCanonicalId != Guid.Empty && decision.SurvivorCanonicalId != Guid.Empty && decision.OldCanonicalId != decision.SurvivorCanonicalId && oldIds.Add(decision.OldCanonicalId), "Consolidations require unique old IDs and explicit survivors.");
		}
		Require(!manifest.Consolidations!.Any(d => oldIds.Contains(d.SurvivorCanonicalId)), "Consolidation chains and cycles are not allowed.");
		Require(!manifest.Assignments!.Any(d => d.CanonicalId.HasValue && oldIds.Contains(d.CanonicalId.Value)), "Assignments must reference the final survivor.");
		foreach (OfflineUnresolvedDecision decision in manifest.Unresolved!)
		{
			Require(decision is not null, "Null unresolved entries are invalid.");
			Require(decision!.EntityKind is "item" or "template" && decision.EntityId != Guid.Empty && Text(decision.Reason, 2000) && observations.Add((decision.EntityKind, decision.EntityId)), "Unresolved observations must be explicit, unique and separate from assignments.");
		}
		if (manifest.Review is not { } review)
		{
			Require(!requireReviewed, "An independent explicit review is required before apply.");
			return;
		}
		Require(Text(review.ReviewerIdentity, 300) && !string.Equals(review.ReviewerIdentity.Trim(), manifest.ProposedBy.Trim(), StringComparison.OrdinalIgnoreCase), "Reviewer identity must be separate from the proposer.");
		Require(review.ReviewedAt != default && review.ReviewedAt >= manifest.ProposedAt && review.ReviewedAt <= now, "Review requires an actual timestamp after proposal.");
		Require(review.Decisions is not null && review.Decisions.All(d => d is not null && ids.Contains(d.DecisionId) && Text(d.Rationale, 2000)) && review.Decisions.Select(d => d.DecisionId).Distinct().Count() == review.Decisions.Count, "Review must name unique existing decisions and individual rationale.");
		Require(!requireReviewed || ids.SetEquals(review.Decisions!.Select(d => d.DecisionId)), "Every applied decision requires explicit review.");
	}

	public static string Actor(OfflineNormalizationManifest manifest, string manifestSha256)
	{
		Require(manifest.Review is not null && IsSha256(manifestSha256), "Reviewed manifest and digest are required.");
		string actor = $"offline-agent:{manifest.Review!.ReviewerIdentity};manifest-sha256:{manifestSha256}";
		Require(actor.Length <= 450, "Offline provenance actor exceeds the portable limit.");
		return actor;
	}

	public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
	private static void RejectDuplicateProperties(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			HashSet<string> names = new(StringComparer.Ordinal);
			foreach (JsonProperty property in element.EnumerateObject())
			{
				Require(names.Add(property.Name), "Duplicate manifest properties are invalid.");
				RejectDuplicateProperties(property.Value);
			}
		}
		else if (element.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in element.EnumerateArray()) { RejectDuplicateProperties(item); }
		}
	}
	private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
	private static void Require(bool condition, string message) { if (!condition) { throw new InvalidDataException(message); } }
}
