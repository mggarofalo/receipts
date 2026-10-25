using System.Reflection;
using System.Text.Json;
using Infrastructure.Entities.Taxonomy;

namespace Infrastructure.Services;

internal static partial class PortableTaxonomyArchive
{
	internal static T? DeserializeStrict<T>(string json)
	{
		using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
		ValidateShape(document.RootElement, typeof(T));
		return JsonSerializer.Deserialize<T>(json, JsonOptions);
	}
	private static void ValidateShape(JsonElement value, Type type)
	{
		if (value.ValueKind == JsonValueKind.Null) { return; }
		type = Nullable.GetUnderlyingType(type) ?? type;
		if (value.ValueKind == JsonValueKind.Array)
		{
			Type? element = type.IsArray ? type.GetElementType() : type.GetGenericArguments().FirstOrDefault();
			Require(element != null, "Unexpected JSON array.");
			foreach (var item in value.EnumerateArray()) { ValidateShape(item, element!); }
			return;
		}
		if (value.ValueKind != JsonValueKind.Object) { return; }
		var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.GetIndexParameters().Length == 0).ToDictionary(p => p.Name, StringComparer.Ordinal);
		HashSet<string> seen = new(StringComparer.Ordinal);
		foreach (var property in value.EnumerateObject())
		{
			Require(seen.Add(property.Name), "Duplicate JSON field: " + property.Name);
			Require(properties.TryGetValue(property.Name, out var declared), "Unknown JSON field: " + property.Name);
			ValidateShape(property.Value, declared!.PropertyType);
		}
		// These additive compatibility fields were absent in early v10 archives. All original
		// nullable fields remain mandatory: omission must never mean null, false or resurrection.
		foreach (string name in properties.Keys)
		{
			bool optional = type == typeof(PurchaseClassificationEntity)
				&& name is "LegacyCategory" or "LegacySubcategory" or "LegacyResolution";
			Require(optional || seen.Contains(name), "Missing JSON field: " + name);
		}
	}
	private static void ValidateLegacyWriteEvidence(State state, PurchaseClassificationEntity assignment)
	{
		if (assignment.Source != "legacy_write")
		{
			Require(assignment.LegacyCategory == null && assignment.LegacySubcategory == null
				&& assignment.LegacyResolution == null, "Unexpected compatibility classification evidence.");
			return;
		}
		Require(assignment.LegacyResolution is "resolved_path" or "unresolved_path", "Invalid compatibility resolution.");
		if (assignment.LegacyResolution == "unresolved_path")
		{ Require(assignment.CategoryId == null, "Unresolved compatibility assignment has a category."); return; }
		var nodes = state.Nodes.Where(n => n.RevisionId == assignment.TaxonomyRevisionId).ToList();
		var target = nodes.SingleOrDefault(n => n.CategoryId == assignment.CategoryId);
		Require(target != null && !target.Archived, "Resolved compatibility target is missing or archived.");
		if (string.IsNullOrEmpty(assignment.LegacySubcategory))
		{ Require(target!.ParentId == null && target.Name == assignment.LegacyCategory, "Compatibility root differs from evidence."); }
		else
		{
			var parent = nodes.SingleOrDefault(n => n.CategoryId == target!.ParentId);
			Require(parent != null && !parent.Archived && parent.ParentId == null
				&& parent.Name == assignment.LegacyCategory && target!.Name == assignment.LegacySubcategory,
				"Compatibility child differs from evidence.");
		}
	}
}
