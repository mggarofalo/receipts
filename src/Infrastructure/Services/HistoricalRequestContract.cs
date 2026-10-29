using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Normalization;
using Application.Taxonomy;

namespace Infrastructure.Services;

// Inactive contract: these claims do not establish approval or authorize an effect.
internal static class HistoricalRequestContract
{
	internal sealed record MappingDecision(int Version, string Kind, HistoricalGroupKey Group,
			Guid TargetCategoryId, Guid MappingRevisionId, Guid ApprovalTaxonomyRevisionId, string SourceFingerprint);
	internal sealed record Request(int Version, string Kind, string Actor, Guid TaxonomyRevisionId, string Reason,
			HistoricalGroupSelection[] Groups, MappingDecision[]? Decisions);

	internal static MappingDecision ReadDecision(string json)
	{
		var decision = PortableTaxonomyArchive.DeserializeStrict<MappingDecision>(json)
				?? throw new InvalidDataException("Null historical decision.");
		ValidateDecision(decision);
		return decision;
	}

	internal static string RequestHash(Request request)
	{
		if (request is null || request.Groups is null || request.Groups.Any(g => g?.Group is null))
		{ throw new InvalidDataException("Missing historical request or selection."); }
		var groups = request.Groups.OrderBy(g => g.Group.Store, StringComparer.Ordinal)
				.ThenBy(g => g.Group.RawDescription, StringComparer.Ordinal)
				.ThenBy(g => g.Group.ItemCode, StringComparer.Ordinal).ToArray();
		if (request.Version == 0 && request.Kind == "manual" && request.Decisions is null)
		{
			// Exactly the existing unversioned service/archive JSON shape and SHA-256.
			return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
			{ request.Actor, request.TaxonomyRevisionId, request.Reason, Groups = groups })));
		}
		if (request.Version != 1 || request.Kind != "approved-mapping" || request.Decisions is null
				|| string.IsNullOrWhiteSpace(request.Actor) || string.IsNullOrWhiteSpace(request.Reason)
				|| request.TaxonomyRevisionId == Guid.Empty || groups.Length is < 1 or > HistoricalReclassificationPolicy.MaximumSelectedGroups
				|| groups.Select(g => g.Group).Distinct().Count() != groups.Length
				|| request.Decisions.Length != groups.Length)
		{ throw new InvalidDataException("Invalid historical request contract."); }
		Raw(request.Actor); Raw(request.Reason);
		foreach (var decision in request.Decisions) { ValidateDecision(decision); }
		var decisions = request.Decisions.OrderBy(d => d.Group.Store, StringComparer.Ordinal)
				.ThenBy(d => d.Group.RawDescription, StringComparer.Ordinal)
				.ThenBy(d => d.Group.ItemCode, StringComparer.Ordinal).ToArray();
		for (int i = 0; i < groups.Length; i++)
		{
			if (groups[i].Group != decisions[i].Group || groups[i].TargetCategoryId != decisions[i].TargetCategoryId)
			{ throw new InvalidDataException("Historical decision differs from selection."); }
		}
		byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
		{ request.Version, request.Kind, request.Actor, request.TaxonomyRevisionId, request.Reason, Groups = groups, Decisions = decisions });
		byte[] domain = Encoding.UTF8.GetBytes("receipts.historical-request.approved-mapping.v1\0");
		return Convert.ToHexString(SHA256.HashData(domain.Concat(payload).ToArray()));
	}

	private static void ValidateDecision(MappingDecision decision)
	{
		if (decision is null || decision.Version != 1 || decision.Kind != "approved-exact-mapping"
				|| decision.Group is null || decision.TargetCategoryId == Guid.Empty || decision.MappingRevisionId == Guid.Empty
				|| decision.ApprovalTaxonomyRevisionId == Guid.Empty || decision.SourceFingerprint is null
				|| decision.SourceFingerprint.Length != 64 || !decision.SourceFingerprint.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'))
		{ throw new InvalidDataException("Invalid historical mapping decision."); }
		Raw(decision.Group.Store); Raw(decision.Group.RawDescription);
		if (decision.Group.ItemCode is not null) { Raw(decision.Group.ItemCode); }
		try
		{
			var source = NormalizationCatalogPolicy.Normalize(new(decision.Group.Store, decision.Group.RawDescription, decision.Group.ItemCode));
			if (NormalizationCatalogPolicy.Hash(source) != decision.SourceFingerprint)
			{ throw new InvalidDataException("Historical decision source fingerprint differs."); }
		}
		catch (NormalizationCatalogException ex) { throw new InvalidDataException("Invalid historical decision source.", ex); }
	}

	private static void Raw(string text)
	{
		if (text is null || text.Contains('\0')) { throw new InvalidDataException("Invalid historical text."); }
		for (int i = 0; i < text.Length; i++)
		{
			if (!char.IsSurrogate(text[i])) { continue; }
			if (!char.IsHighSurrogate(text[i]) || i + 1 >= text.Length || !char.IsLowSurrogate(text[++i]))
			{ throw new InvalidDataException("Unpaired surrogate in historical text."); }
		}
	}
}
