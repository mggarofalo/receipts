using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Infrastructure.Entities.Taxonomy;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	// Candidate format limits, checked before parsing untrusted JSON or decimal exponents.
	internal const int MaximumEffectDecisionBytes = 1024 * 1024;
	internal const int MaximumEffectNumberCharacters = 4096;
	internal const int MaximumEffectPayloadBytes = 8 * 1024 * 1024;
	// Candidate representation only: deliberately not wired into the current archive version.
	// Membership is sealed by the exporter, not by deployment-local transaction stamps.
	internal sealed record EffectManifest(OrganizationEffectManifestEntity Manifest, int ItemCount, string ItemsHash);
	internal sealed record EffectEvidence(List<EffectManifest> Manifests,
		List<OrganizationEffectItemEntity> Items, List<OrganizationEffectOutcomeEntity> Outcomes);
	internal sealed record EffectRecord(string Kind, string Id, string Payload);

	internal static EffectManifest SealEffectManifest(OrganizationEffectManifestEntity manifest,
		IEnumerable<OrganizationEffectItemEntity> items)
	{
		var rows = items.ToList();
		Require(rows.All(i => i.ManifestId == manifest.Id), "Effect membership belongs to another manifest.");
		return new(manifest, rows.Count, EffectItemsHash(rows));
	}

	internal static EffectEvidence ReadEffectEvidence(IEnumerable<EffectRecord> records)
	{
		EffectEvidence result = new([], [], []);
		HashSet<(string, string)> seen = [];
		foreach (var row in records)
		{
			Require(row != null && row.Kind is "manifest" or "item" or "outcome"
				&& row.Id != null && row.Id.Length <= 256, "Invalid effect record envelope.");
			Require(row!.Payload != null && row.Payload.Length <= MaximumEffectPayloadBytes
				&& Encoding.UTF8.GetByteCount(row.Payload) <= MaximumEffectPayloadBytes,
				"Effect record payload exceeds size limit.");
			Require(seen.Add((row.Kind, row.Id!)), "Duplicate effect record key.");
			object value = row.Kind switch
			{
				"manifest" => DeserializeStrict<EffectManifest>(row.Payload!) ?? throw new InvalidDataException("Null effect manifest."),
				"item" => DeserializeStrict<OrganizationEffectItemEntity>(row.Payload!) ?? throw new InvalidDataException("Null effect item."),
				"outcome" => DeserializeStrict<OrganizationEffectOutcomeEntity>(row.Payload!) ?? throw new InvalidDataException("Null effect outcome."),
				_ => throw new InvalidDataException("Unknown effect record kind.")
			};
			Require(row.Id == EffectKey(value), "Effect archive key differs from payload.");
			switch (value)
			{
				case EffectManifest manifest: result.Manifests.Add(manifest); break;
				case OrganizationEffectItemEntity item: result.Items.Add(item); break;
				case OrganizationEffectOutcomeEntity outcome: result.Outcomes.Add(outcome); break;
			}
		}
		return result;
	}

	internal static List<EffectRecord> WriteEffectEvidence(EffectEvidence evidence) =>
		[.. evidence.Manifests.Select(x => EffectRow("manifest", x)),
		 .. evidence.Items.Select(x => EffectRow("item", x)),
		 .. evidence.Outcomes.Select(x => EffectRow("outcome", x))];

	private static EffectRecord EffectRow<T>(string kind, T value) where T : notnull =>
		new(kind, EffectKey(value), JsonSerializer.Serialize(value, JsonOptions));

	private static string EffectKey(object value) => value switch
	{
		EffectManifest x when x.Manifest is not null => JsonSerializer.Serialize(x.Manifest.Id),
		OrganizationEffectItemEntity x => JsonSerializer.Serialize(new { x.ManifestId, x.ItemId }),
		OrganizationEffectOutcomeEntity x => JsonSerializer.Serialize(new { x.ManifestId, x.ItemId, x.Phase }),
		_ => throw new InvalidDataException("Unknown effect record type.")
	};

	// Checks effect-specific closure against retained taxonomy/assignment evidence. The archive
	// integration must also validate the ordinary taxonomy history and business dependencies.
	internal static void ValidateEffectEvidence(State retained, EffectEvidence evidence)
	{
		Require(evidence.Manifests.All(m => m is not null && m.Manifest is not null)
			&& evidence.Items.All(i => i is not null) && evidence.Outcomes.All(o => o is not null), "Null effect evidence.");
		Require(evidence.Manifests.Select(m => m.Manifest.Id).Distinct().Count() == evidence.Manifests.Count
			&& evidence.Items.Select(i => (i.ManifestId, i.ItemId)).Distinct().Count() == evidence.Items.Count
			&& evidence.Outcomes.Select(o => (o.ManifestId, o.ItemId, o.Phase)).Distinct().Count() == evidence.Outcomes.Count,
			"Duplicate effect evidence key.");
		var manifests = evidence.Manifests.ToDictionary(m => m.Manifest.Id);
		var items = evidence.Items.ToDictionary(i => (i.ManifestId, i.ItemId));
		var outcomes = evidence.Outcomes.ToDictionary(o => (o.ManifestId, o.ItemId, o.Phase));
		var assignments = retained.Assignments.ToDictionary(a => a.Id);
		HashSet<Guid> operations = [.. retained.HistoricalItems.SelectMany(i => new[] { i.ApplyOperationId, i.UndoOperationId })];
		foreach (var sealedManifest in evidence.Manifests)
		{
			var m = sealedManifest.Manifest;
			Require(m.Id != Guid.Empty && Text(m.Actor) && Text(m.PolicyVersion) && Hash(m.RequestHash) && Utc(m.CapturedAt)
				&& retained.Revisions.Any(r => r.Id == m.TaxonomyRevisionId && r.RecordedAt <= m.CapturedAt), "Invalid effect manifest metadata.");
			var members = evidence.Items.Where(i => i.ManifestId == m.Id).ToList();
			Require(sealedManifest.ItemCount >= 0 && sealedManifest.ItemCount == members.Count
				&& sealedManifest.ItemsHash == EffectItemsHash(members), "Effect sealed membership differs from evidence.");
		}
		foreach (var i in evidence.Items)
		{
			Require(manifests.TryGetValue(i.ManifestId, out var sealedManifest), "Orphan effect item.");
			var m = sealedManifest!.Manifest;
			Require(i.ItemId != Guid.Empty && i.ReceiptId != Guid.Empty && i.ItemRevision != Guid.Empty && i.ReceiptRevision != Guid.Empty
				&& i.Store != null && i.RawDescription != null && !i.Store.Contains('\0') && !i.RawDescription.Contains('\0')
				&& i.ItemCode?.Contains('\0') != true && i.TaxonomyRevisionId == m.TaxonomyRevisionId
				&& retained.Nodes.Any(n => n.RevisionId == i.TaxonomyRevisionId && n.CategoryId == i.TargetCategoryId && !n.Archived),
				"Invalid frozen effect item.");
			Require(i.ApplyOperationId != Guid.Empty && i.UndoOperationId != Guid.Empty
				&& operations.Add(i.ApplyOperationId) && operations.Add(i.UndoOperationId), "Effect operation reservation collision.");
			Require(i.ExpectedAssignmentId is not Guid prior || assignments.TryGetValue(prior, out var previous)
				&& previous.ReceiptItemId == i.ItemId && previous.RecordedAt <= m.CapturedAt, "Effect prior assignment differs from evidence.");
			CanonicalEffectDecision(i.DecisionEvidenceJson);
			foreach (string phase in new[] { "apply", "undo" })
			{
				Guid operationId = phase == "apply" ? i.ApplyOperationId : i.UndoOperationId;
				outcomes.TryGetValue((i.ManifestId, i.ItemId, phase), out var outcome);
				var operationAssignments = retained.Assignments.Where(a => a.OperationId == operationId).ToList();
				Require(operationAssignments.All(a => outcome?.Disposition == "applied" && a.Id == outcome.AssignmentId),
					"Unapplied effect operation has retained assignment evidence.");
				if (outcome == null) { continue; }
				Require(outcome.Disposition is "applied" or "skipped" && Text(outcome.Reason) && Utc(outcome.RecordedAt)
					&& outcome.RecordedAt >= m.CapturedAt && outcome.ItemRevision != Guid.Empty && outcome.ReceiptRevision != Guid.Empty,
					"Invalid effect outcome metadata.");
				outcomes.TryGetValue((i.ManifestId, i.ItemId, "apply"), out var applied);
				Require(phase != "undo" || applied?.Disposition == "applied" && outcome.RecordedAt >= applied.RecordedAt,
					"Effect undo lacks applied evidence.");
				if (outcome.Disposition == "skipped")
				{ Require(outcome.AssignmentId == null, "Skipped effect has assignment evidence."); continue; }
				Guid? priorId = phase == "apply" ? i.ExpectedAssignmentId : applied!.AssignmentId;
				Guid? category = phase == "apply" ? i.TargetCategoryId
					: i.ExpectedAssignmentId is Guid expected ? assignments[expected].CategoryId : null;
				Require(outcome.ItemRevision is not null && outcome.ReceiptRevision is not null
					&& outcome.AssignmentId is Guid id && assignments.TryGetValue(id, out var a)
					&& a.ReceiptItemId == i.ItemId && a.Actor == m.Actor && a.OperationId == operationId
					&& a.PreviousAssignmentId == priorId && a.CategoryId == category && a.RecordedAt == outcome.RecordedAt
					&& retained.Revisions.Any(r => r.Id == a.TaxonomyRevisionId && r.RecordedAt <= a.RecordedAt)
					&& (phase != "apply" || a.TaxonomyRevisionId == i.TaxonomyRevisionId), "Effect outcome assignment lineage differs from evidence.");
			}
		}
		Require(evidence.Outcomes.All(o => items.ContainsKey((o.ManifestId, o.ItemId)) && o.Phase is "apply" or "undo"), "Orphan or unknown effect outcome phase.");
	}

	// Structural JSON canonicalization, not a policy schema or RFC 8785 implementation.
	// Objects sort ordinally, arrays retain order, and numbers use exact decimal value semantics.
	// RequestHash remains opaque until a versioned request/policy contract exists.
	internal static string CanonicalEffectDecision(string json)
	{
		Require(json != null, "Null effect decision evidence.");
		Require(json!.Length <= MaximumEffectDecisionBytes && Encoding.UTF8.GetByteCount(json) <= MaximumEffectDecisionBytes,
			"Effect decision evidence exceeds size limit.");
		using var document = JsonDocument.Parse(json!, new JsonDocumentOptions { MaxDepth = 32 });
		Require(document.RootElement.ValueKind == JsonValueKind.Object, "Effect decision evidence must be an object.");
		using var output = new MemoryStream();
		using (var writer = new Utf8JsonWriter(output)) { Write(document.RootElement, writer); }
		return Encoding.UTF8.GetString(output.ToArray());
		static void Write(JsonElement element, Utf8JsonWriter writer)
		{
			if (element.ValueKind == JsonValueKind.Object)
			{
				writer.WriteStartObject();
				HashSet<string> keys = new(StringComparer.Ordinal);
				foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
				{
					Require(keys.Add(property.Name), "Duplicate effect decision JSON field.");
					writer.WritePropertyName(property.Name); Write(property.Value, writer);
				}
				writer.WriteEndObject();
			}
			else if (element.ValueKind == JsonValueKind.Array)
			{ writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) { Write(item, writer); } writer.WriteEndArray(); }
			else if (element.ValueKind == JsonValueKind.Number)
			{ writer.WriteRawValue(CanonicalEffectNumber(element.GetRawText())); }
			else { element.WriteTo(writer); }
		}
	}

	// Exact coefficient/exponent normalization makes hashes stable across jsonb's numeric
	// rendering (including exponent expansion and retained decimal scale). Never use double
	// or decimal: neither can represent PostgreSQL numeric's complete precision/range.
	private static string CanonicalEffectNumber(string token)
	{
		Require(token.Length <= MaximumEffectNumberCharacters, "Effect decision number exceeds size limit.");
		bool negative = token[0] == '-';
		string unsigned = negative ? token[1..] : token;
		int exponentAt = unsigned.IndexOfAny(['e', 'E']);
		BigInteger exponent = exponentAt < 0 ? BigInteger.Zero
			: BigInteger.Parse(unsigned[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
		string mantissa = exponentAt < 0 ? unsigned : unsigned[..exponentAt];
		int decimalAt = mantissa.IndexOf('.');
		if (decimalAt >= 0) { exponent -= mantissa.Length - decimalAt - 1; }
		string digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
		if (digits.Length == 0) { return "0"; }
		int significantLength = digits.Length;
		while (digits[significantLength - 1] == '0') { significantLength--; }
		exponent += digits.Length - significantLength;
		string coefficient = (negative ? "-" : "") + digits[..significantLength];
		return exponent.IsZero ? coefficient : coefficient + "e" + exponent.ToString(CultureInfo.InvariantCulture);
	}

	private static string EffectItemsHash(IEnumerable<OrganizationEffectItemEntity> items)
	{
		var canonical = items.OrderBy(i => i.ItemId).Select(i =>
		{
			var node = JsonSerializer.SerializeToNode(i, JsonOptions)!.AsObject();
			node[nameof(i.DecisionEvidenceJson)] = CanonicalEffectDecision(i.DecisionEvidenceJson);
			return node;
		}).ToArray();
		return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical, JsonOptions)));
	}
}
