using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Taxonomy;

namespace Application.Taxonomy;

/// <summary>Pure preflight only. Does not establish database readiness or append history.</summary>
public static class LegacyTaxonomyMigrationPlanner
{
	private const string Version = "receipts-legacy-taxonomy-v1";

	public static LegacyMigrationPlan Plan(LegacyMigrationRequest request, LegacyMigrationPlan? previous = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceDataset);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationKind);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.Actor);
		if (request.OperationId == Guid.Empty) { throw new ArgumentException("Operation ID must not be empty.", nameof(request)); }
		LegacyCategoryEvidence[] categories = request.Categories.OrderBy(row => row.Id).ToArray();
		LegacySubcategoryEvidence[] subcategories = request.Subcategories.OrderBy(row => row.Id).ToArray();
		LegacyItemEvidence[] items = request.Items.OrderBy(row => row.Id).ToArray();
		Dictionary<Guid, LegacyItemEvidence> itemsById = items.ToDictionary(row => row.Id);
		ValidateIds(categories.Select(row => row.Id));
		ValidateIds(subcategories.Select(row => row.Id));
		ValidateIds(items.Select(row => row.Id));
		string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
		{
			Version,
			request.SourceDataset,
			request.OperationKind,
			request.Actor,
			request.ObservedAt,
			Categories = categories,
			Subcategories = subcategories,
			Items = items
		}))));
		if (previous != null)
		{
			if (previous.OperationId != request.OperationId || previous.RequestHash != hash)
			{ throw new LegacyMigrationReplayException(); }
			return previous;
		}

		List<LegacyTaxonomyCrosswalk> crosswalks = [];
		List<LegacyMigrationException> exceptions = [];
		Dictionary<Guid, LegacyPlannedNode> nodes = [];
		Dictionary<Guid, HashSet<Guid>> observedEvidence = [];
		Dictionary<Guid, LegacyCategoryEvidence> roots = categories.ToDictionary(row => row.Id);
		Guid Identity(params string?[] parts) => StableId([Version, request.SourceDataset, .. parts]);
		Guid CategoryId(Guid id) => Identity("category", id.ToString("D"));
		Guid SubcategoryId(Guid id) => Identity("subcategory", id.ToString("D"));
		foreach (LegacyCategoryEvidence row in categories)
		{
			string? error = ScalarError(row.Name, row.Definition);
			Guid id = CategoryId(row.Id);
			crosswalks.Add(new(LegacyTaxonomySource.Category, row.Id, id, row, null, error));
			if (error != null) { exceptions.Add(new(error, true, LegacyTaxonomySource.Category, row.Id)); }
			else { nodes.Add(id, new(new(id, null, row.Name!, row.Definition, Archived(row.IsActive, row.Deletion)), false, [])); }
		}
		foreach (LegacySubcategoryEvidence row in subcategories)
		{
			Guid id = SubcategoryId(row.Id);
			Guid parentId = CategoryId(row.CategoryId);
			string? error = ScalarError(row.Name, row.Definition);
			if (!roots.ContainsKey(row.CategoryId)) { error = "orphan_suggestion_parent"; }
			else if (!nodes.ContainsKey(parentId)) { error ??= "invalid_suggestion_parent"; }
			crosswalks.Add(new(LegacyTaxonomySource.Subcategory, row.Id, id, null, row, error));
			if (error != null) { exceptions.Add(new(error, true, LegacyTaxonomySource.Subcategory, row.Id)); }
			else
			{
				nodes.Add(id, new(new(id, parentId, row.Name!, row.Definition,
				nodes[parentId].Node.Archived || Archived(row.IsActive, row.Deletion)), false, []));
			}
		}

		// Lookup includes archived and invalid suggestions: selecting the sole active
		// candidate would invent historical identity in the presence of duplicates.
		ILookup<string?, LegacyCategoryEvidence> rootNames = categories.ToLookup(row => row.Name, StringComparer.Ordinal);
		ILookup<(Guid, string?), LegacySubcategoryEvidence> childNames = subcategories.ToLookup(row => (row.CategoryId, row.Name));
		List<LegacyItemResolution> outcomes = [];
		foreach (LegacyItemEvidence item in items)
		{
			Guid? target = null;
			string? error = null;
			LegacyResolutionKind kind = LegacyResolutionKind.Unclassified;
			if (string.IsNullOrEmpty(item.Category))
			{
				if (!string.IsNullOrEmpty(item.Subcategory)) { error = "child_without_category"; }
			}
			else if (ScalarError(item.Category, null) != null || (!string.IsNullOrEmpty(item.Subcategory) && ScalarError(item.Subcategory, null) != null))
			{ error = "invalid_item_label"; }
			else
			{
				// Resolution only distinguishes zero, one and multiple candidates.
				// Bound inspection even when a dirty source contains many duplicates.
				LegacyCategoryEvidence[] candidates = rootNames[item.Category].Take(2).ToArray();
				kind = LegacyResolutionKind.ResolvedSuggestion;
				if (candidates.Length > 1) { error = "ambiguous_category"; }
				else
				{
					Guid rootId = candidates.Length == 1 ? CategoryId(candidates[0].Id) : Observed(null, item.Category, item);
					if (!nodes.ContainsKey(rootId)) { error = "invalid_suggestion"; }
					else if (string.IsNullOrEmpty(item.Subcategory)) { target = rootId; }
					else
					{
						LegacySubcategoryEvidence[] children = candidates.Length == 1 ? childNames[(candidates[0].Id, item.Subcategory)].Take(2).ToArray() : [];
						if (children.Length > 1) { error = "ambiguous_subcategory"; }
						else
						{
							Guid childId = children.Length == 1 ? SubcategoryId(children[0].Id) : Observed(rootId, item.Subcategory, item);
							if (!nodes.ContainsKey(childId)) { error = "invalid_suggestion"; }
							else { target = childId; }
						}
					}
					if (target != null && nodes[target.Value].ObservedOnly) { kind = LegacyResolutionKind.ResolvedObservedPath; }
				}
			}
			if (error != null)
			{
				kind = LegacyResolutionKind.Unresolved;
				exceptions.Add(new(error, false, ItemId: item.Id));
			}
			if (item.ProductLinkException != null) { exceptions.Add(new("product_link_exception", false, ItemId: item.Id)); }
			outcomes.Add(new(Identity("assignment", request.OperationKind, request.OperationId.ToString("D"), item.Id.ToString("D")), item, target, kind, error));
		}

		// Finalize observed availability after all rows, so input order never matters.
		foreach (Guid id in nodes.Keys.ToArray())
		{
			LegacyPlannedNode node = nodes[id];
			if (!node.ObservedOnly) { continue; }
			bool archived = observedEvidence[id].All(itemId => Trashed(itemsById[itemId]));
			if (node.Node.ParentId is Guid parent && !nodes[parent].ObservedOnly) { archived |= nodes[parent].Node.Archived; }
			nodes[id] = node with { Node = node.Node with { Archived = archived }, ObservedItemIds = observedEvidence[id].Order().ToArray() };
		}
		int duplicatePaths = 0;
		foreach (IGrouping<(Guid?, string), LegacyPlannedNode> group in nodes.Values.GroupBy(row => (row.Node.ParentId, row.Node.Name)))
		{
			if (group.Count() > 1) { duplicatePaths++; }
			if (group.Count(row => !row.Node.Archived) > 1) { exceptions.Add(new("duplicate_selectable_siblings", true)); }
		}
		if (!exceptions.Any(exception => exception.BlocksCommit)) { TaxonomyTree.Apply(nodes.Values.Select(row => row.Node).ToArray(), []); }
		LegacyMigrationCounts counts = new(categories.Length, subcategories.Length, items.Length,
			categories.Count(row => row.Deletion.DeletedAt != null) + subcategories.Count(row => row.Deletion.DeletedAt != null),
			items.Count(Trashed), outcomes.Count(row => row.CategoryId != null),
			outcomes.Count(row => row.Kind == LegacyResolutionKind.Unclassified), outcomes.Count(row => row.Kind == LegacyResolutionKind.Unresolved),
			duplicatePaths, items.Count(row => row.ProductLinkException != null),
			items.GroupBy(row => row.Currency, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
				.Select(group => new LegacyCurrencyTotal(group.Key, group.Sum(row => row.Amount))).ToArray());
		return new(request.SourceDataset, request.OperationId, request.OperationKind, request.Actor, request.ObservedAt, hash,
			Identity("revision", request.OperationKind, request.OperationId.ToString("D")), nodes.Values.OrderBy(row => row.Node.Id).ToArray(),
			crosswalks.ToArray(), outcomes.ToArray(), exceptions.ToArray(), counts);

		Guid Observed(Guid? parent, string name, LegacyItemEvidence item)
		{
			Guid id = Identity("observed", parent?.ToString("D"), name);
			if (!nodes.TryGetValue(id, out LegacyPlannedNode? node))
			{
				node = new(new(id, parent, name, null, false), true, []);
				nodes.Add(id, node);
				observedEvidence.Add(id, []);
			}
			observedEvidence[id].Add(item.Id);
			return id;
		}
	}

	private static bool Archived(bool active, LegacyDeletionState deletion) => !active || deletion.DeletedAt != null;
	private static bool Trashed(LegacyItemEvidence item) => item.Deletion.DeletedAt != null || item.ReceiptDeletion.DeletedAt != null;
	private static void ValidateIds(IEnumerable<Guid> ids)
	{
		HashSet<Guid> seen = [];
		foreach (Guid id in ids)
		{ if (id == Guid.Empty || !seen.Add(id)) { throw new ArgumentException("Source IDs must be nonempty and unique within each source table."); } }
	}

	private static string? ScalarError(string? name, string? definition)
	{
		try { TaxonomyTree.Apply([new(new Guid("a6f3ce78-aa88-4eb4-8574-a521e0000001"), null, name!, definition, false)], []); }
		catch (TaxonomyValidationException) { return "invalid_legacy_scalar"; }
		return null;
	}

	// RFC 9562 UUIDv8: custom SHA-256 namespace, canonical JSON framing and UTF-8.
	// Table qualification prevents category/subcategory UUID collisions.
	private static Guid StableId(string?[] parts)
	{
		byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts)));
		digest[6] = (byte)((digest[6] & 0x0f) | 0x80);
		digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
		return new Guid(digest.AsSpan(0, 16), bigEndian: true);
	}
}
