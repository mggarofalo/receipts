using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Application.Normalization;

public sealed record CatalogAssignmentFacts(string Origin, bool ItemLive, bool ParentLive,
	bool TargetRejected, bool SourceRejected, bool TemplateLinked, bool RevokedMappingOwned);
public sealed record CatalogAssignmentDecision(string Disposition, bool Protected, bool Eligible);

public static class NormalizationCatalogPolicy
{
	public const int ChunkSize = 100;
	public static NormalizationSourceKey Normalize(NormalizationSourceKey source)
	{
		// PostgreSQL btrim(text), used by the grouped read projections, removes U+0020 only.
		// Preserve tabs/NBSP/other Unicode characters rather than silently hashing a different key.
		string location = source.Location.Trim(' ');
		string description = source.Description.Trim(' ');
		string? code = source.ItemCode?.Trim(' ');
		if (code?.Length == 0) { code = null; }
		if (description.Length is < 1 or > 2000 || location.Length > 1000 || code?.Length > 200)
		{
			throw new NormalizationCatalogException(400, "invalid_source", "Select a valid nonempty source description.");
		}
		return new(location, description, code);
	}
	public static string Hash(NormalizationSourceKey source)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Normalize(source)))));
	public static CatalogAssignmentDecision Classify(CatalogAssignmentFacts facts, bool includeLegacy)
	{
		if (!facts.ItemLive || !facts.ParentLive) { return new("unavailable", true, false); }
		if (facts.TargetRejected || facts.SourceRejected) { return new("rejected", true, false); }
		if (facts.TemplateLinked || facts.Origin is "manual" or "template") { return new("protected", true, false); }
		if (facts.Origin == "reviewed") { return new("reviewed", true, false); }
		if (facts.Origin is "mapped" or "excluded")
		{
			return facts.RevokedMappingOwned ? new("revisable", false, true) : new(facts.Origin, true, false);
		}
		return facts.Origin switch
		{
			"unassigned" or "automatic" => new(facts.Origin, false, true),
			"legacy_unknown" => new("legacy_unknown", false, includeLegacy),
			_ => new("unrecognized", true, false)
		};
	}

	public static void RequireAction(string state, string action)
	{
		bool allowed = action switch
		{
			"approve" => state == "preview",
			"resume" => state is "applying" or "paused" or "undoing" or "undo_paused" or "completed" or "undone",
			"cancel" => state is "preview" or "applying" or "paused" or "undoing" or "undo_paused",
			"undo" => state is "applying" or "paused" or "completed" or "cancelled" or "undoing" or "undo_paused" or "undone",
			_ => false
		};
		if (!allowed) { throw new NormalizationCatalogException(409, "operation_state", $"The {action} action is not available for this operation state."); }
	}
}

public sealed class NormalizationCatalogException(int statusCode, string code, string detail, IReadOnlyDictionary<string, object?>? extensions = null) : Exception(detail)
{
	public int StatusCode { get; } = statusCode;
	public string Code { get; } = code;
	public IReadOnlyDictionary<string, object?> Extensions { get; } = extensions ?? new Dictionary<string, object?>();
}
