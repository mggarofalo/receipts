using Domain.Taxonomy;

namespace Application.Taxonomy;

// Provider and scope are opaque, case-sensitive identifiers (for YNAB, scope is the budget ID).
public sealed record ExternalCategoryScope(string Provider, string ScopeId);
public sealed record ExternalCategoryMapping(Guid Id, Guid CategoryId, ExternalCategoryScope Scope,
		string ExternalCategoryId);

public enum ExternalCategoryAncestry { RecordedClassification, CurrentTaxonomy }
public enum ExternalCategoryMappingDisposition { Unassigned, InvalidAncestry, Unmapped, AmbiguousMapping, Mapped }
public enum LegacyExternalCategoryDisposition { Unmatched, Ambiguous, Unique }

public sealed record ExternalCategoryMappingResolution(ExternalCategoryMappingDisposition Disposition,
		Guid? CategoryId, Guid? TaxonomyRevisionId, ExternalCategoryAncestry Ancestry,
		ExternalCategoryMapping? Mapping = null, int? AncestorDistance = null);

public sealed record LegacyExternalCategoryBinding(LegacyExternalCategoryDisposition Disposition,
		string LegacyName, IReadOnlyList<Guid> CandidateCategoryIds, Guid? CategoryId = null);

/// <summary>Pure external export mapping policy; never assigns or reclassifies a purchase.</summary>
public static class ExternalCategoryMappingPolicy
{
	/// <summary>Resolve using only the immutable ancestry recorded with the assignment.</summary>
	public static ExternalCategoryMappingResolution ResolveRecorded(CurrentClassification? classification,
			ExternalCategoryScope scope, IReadOnlyList<ExternalCategoryMapping> mappings)
	{
		Guid? categoryId = classification?.CategoryId;
		Guid? revisionId = classification?.TaxonomyRevisionId;
		const ExternalCategoryAncestry ancestry = ExternalCategoryAncestry.RecordedClassification;
		if (categoryId is null)
		{
			return new(ExternalCategoryMappingDisposition.Unassigned, null, revisionId, ancestry);
		}

		IReadOnlyList<TaxonomyNode> path = classification!.RecordedPath;
		HashSet<Guid> visited = [];
		Guid? parentId = null;
		foreach (TaxonomyNode node in path)
		{
			if (!visited.Add(node.Id) || node.ParentId != parentId)
			{
				return new(ExternalCategoryMappingDisposition.InvalidAncestry, categoryId, revisionId, ancestry);
			}
			parentId = node.Id;
		}
		if (path.Count == 0 || path[^1].Id != categoryId)
		{
			return new(ExternalCategoryMappingDisposition.InvalidAncestry, categoryId, revisionId, ancestry);
		}

		// Archived nodes remain valid historical evidence; current previews reject them below.
		return ResolvePath(categoryId.Value, revisionId, ancestry, path, scope, mappings);
	}

	/// <summary>Explicit current-tree preview. Callers must not substitute it for recorded resolution.</summary>
	public static ExternalCategoryMappingResolution ResolveCurrent(Guid? categoryId, Guid taxonomyRevisionId,
			IReadOnlyList<TaxonomyNode> nodes, ExternalCategoryScope scope,
			IReadOnlyList<ExternalCategoryMapping> mappings)
	{
		const ExternalCategoryAncestry ancestry = ExternalCategoryAncestry.CurrentTaxonomy;
		if (categoryId is null)
		{
			return new(ExternalCategoryMappingDisposition.Unassigned, null, taxonomyRevisionId, ancestry);
		}

		Dictionary<Guid, TaxonomyNode> byId = [];
		foreach (TaxonomyNode node in nodes)
		{
			if (!byId.TryAdd(node.Id, node))
			{
				return new(ExternalCategoryMappingDisposition.InvalidAncestry, categoryId, taxonomyRevisionId, ancestry);
			}
		}
		HashSet<Guid> visited = [];
		List<TaxonomyNode> path = [];
		Guid? current = categoryId;
		while (current is Guid id)
		{
			if (!visited.Add(id) || !byId.TryGetValue(id, out TaxonomyNode? node) || node.Archived)
			{
				return new(ExternalCategoryMappingDisposition.InvalidAncestry, categoryId, taxonomyRevisionId, ancestry);
			}
			path.Add(node);
			current = node.ParentId;
		}
		path.Reverse();
		return ResolvePath(categoryId.Value, taxonomyRevisionId, ancestry, path, scope, mappings);
	}

	/// <summary>Migration evidence only: retain exact legacy text and every distinct matching identity,
	/// including archived identities. Never guess by case, whitespace, or a display path.</summary>
	public static LegacyExternalCategoryBinding BindLegacyName(string name, IReadOnlyList<TaxonomyNode> nodes)
	{
		Guid[] candidates = nodes.Where(node => string.Equals(node.Name, name, StringComparison.Ordinal))
				.Select(node => node.Id).Distinct().Order().ToArray();
		LegacyExternalCategoryDisposition disposition = candidates.Length switch
		{
			0 => LegacyExternalCategoryDisposition.Unmatched,
			1 => LegacyExternalCategoryDisposition.Unique,
			_ => LegacyExternalCategoryDisposition.Ambiguous
		};
		return new(disposition, name, Array.AsReadOnly(candidates), candidates.Length == 1 ? candidates[0] : null);
	}

	private static ExternalCategoryMappingResolution ResolvePath(Guid categoryId, Guid? taxonomyRevisionId,
			ExternalCategoryAncestry ancestry, IReadOnlyList<TaxonomyNode> path, ExternalCategoryScope scope,
			IReadOnlyList<ExternalCategoryMapping> mappings)
	{
		for (int index = path.Count - 1; index >= 0; index--)
		{
			ExternalCategoryMapping[] matches = mappings.Where(mapping => mapping.Scope == scope
					&& mapping.CategoryId == path[index].Id).Take(2).ToArray();
			if (matches.Length > 1)
			{
				return new(ExternalCategoryMappingDisposition.AmbiguousMapping, categoryId, taxonomyRevisionId, ancestry);
			}
			if (matches.Length == 1)
			{
				return new(ExternalCategoryMappingDisposition.Mapped, categoryId, taxonomyRevisionId,
						ancestry, matches[0], path.Count - 1 - index);
			}
		}
		return new(ExternalCategoryMappingDisposition.Unmapped, categoryId, taxonomyRevisionId, ancestry);
	}
}
