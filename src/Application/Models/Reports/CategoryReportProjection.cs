using Domain.Taxonomy;

namespace Application.Models.Reports;

public enum CategoryReportView { AsClassified, CurrentHierarchy }
public enum CategoryReportAssignmentSelection { CurrentPointer, LatestRecordedAtOrBeforeCutoff }
public enum CategoryReportRollup { AssignedCategory, Root }
public enum CategoryReportState { Classified, Unclassified, MissingAssignment, UnresolvedLegacy, UnresolvedPath, BeforeMigrationHistory }

/// <summary>Aggregated purchase facts, after all receipt/item filters and the requested assignment selection policy.</summary>
public sealed record CategoryReportFact(Guid? CategoryId, Guid? AssignmentRevisionId,
	CategoryReportState State, string Currency, decimal Amount, int PurchaseCount);
public sealed record CategoryReportNode(Guid RevisionId, TaxonomyNode Node);
public sealed record CategoryReportBucket(Guid? PathRevisionId, Guid? CategoryId,
	CategoryReportState State, IReadOnlyList<TaxonomyNode> Path, string Currency, decimal Amount, int PurchaseCount);
public sealed record CategoryReportResult(CategoryReportView View, Guid? CurrentRevisionId,
	IReadOnlyList<CategoryReportBucket> Buckets, CategoryReportRollup Rollup,
	DateTimeOffset? KnowledgeCutoff, CategoryReportAssignmentSelection AssignmentSelection);

/// <summary>Read-only regrouping: never changes assignments or resolves identities using display text.</summary>
public static class CategoryReportProjection
{
	public static CategoryReportResult Project(IEnumerable<CategoryReportFact> facts,
		IEnumerable<CategoryReportNode> nodes, CategoryReportView view, Guid? currentRevisionId,
		CategoryReportRollup rollup = CategoryReportRollup.AssignedCategory, Guid? categoryFilter = null,
		DateTimeOffset? knowledgeCutoff = null)
	{
		if (!Enum.IsDefined(view)) { throw new ArgumentOutOfRangeException(nameof(view)); }
		if (!Enum.IsDefined(rollup)) { throw new ArgumentOutOfRangeException(nameof(rollup)); }
		Dictionary<(Guid, Guid), TaxonomyNode> byId = nodes.ToDictionary(row => (row.RevisionId, row.Node.Id), row => row.Node);
		Dictionary<(Guid, Guid), IReadOnlyList<TaxonomyNode>?> paths = [];
		Dictionary<(Guid?, Guid?, CategoryReportState, string), CategoryReportBucket> buckets = [];
		foreach (CategoryReportFact fact in facts)
		{
			Guid? revision = view == CategoryReportView.AsClassified ? fact.AssignmentRevisionId : currentRevisionId;
			CategoryReportState state = fact.State;
			IReadOnlyList<TaxonomyNode> path = [];
			if (state == CategoryReportState.Classified)
			{
				if (revision is Guid revisionId && fact.CategoryId is Guid categoryId)
				{
					if (!paths.TryGetValue((revisionId, categoryId), out IReadOnlyList<TaxonomyNode>? resolved))
					{
						List<TaxonomyNode> reverse = [];
						HashSet<Guid> visited = [];
						Guid? cursor = categoryId;
						bool valid = true;
						while (cursor is Guid id)
						{
							if (!visited.Add(id) || !byId.TryGetValue((revisionId, id), out TaxonomyNode? node))
							{ valid = false; break; }
							reverse.Add(node);
							cursor = node.ParentId;
						}
						reverse.Reverse();
						resolved = valid ? reverse.AsReadOnly() : null;
						paths.Add((revisionId, categoryId), resolved);
					}
					if (resolved != null) { path = resolved; }
					else { state = CategoryReportState.UnresolvedPath; }
				}
				else { state = CategoryReportState.UnresolvedPath; }
			}
			// Parent filtering includes direct assignments and descendants exactly once.
			if (categoryFilter.HasValue && !path.Any(node => node.Id == categoryFilter.Value)) { continue; }
			Guid? bucketCategory = fact.CategoryId;
			if (state == CategoryReportState.Classified && rollup == CategoryReportRollup.Root)
			{
				path = new[] { path[0] };
				bucketCategory = path[0].Id;
			}
			// Missing/unclassified buckets do not imply a historical path. Broken paths retain their IDs.
			Guid? pathRevision = state is CategoryReportState.Classified or CategoryReportState.UnresolvedPath ? revision : null;
			var key = (pathRevision, bucketCategory, state, fact.Currency);
			if (buckets.TryGetValue(key, out CategoryReportBucket? existing))
			{ buckets[key] = existing with { Amount = existing.Amount + fact.Amount, PurchaseCount = existing.PurchaseCount + fact.PurchaseCount }; }
			else { buckets.Add(key, new(pathRevision, bucketCategory, state, path, fact.Currency, fact.Amount, fact.PurchaseCount)); }
		}
		return new(view, view == CategoryReportView.CurrentHierarchy ? currentRevisionId : null,
			buckets.Values.OrderBy(bucket => bucket.State).ThenBy(bucket => bucket.PathRevisionId)
				.ThenBy(bucket => bucket.CategoryId).ThenBy(bucket => bucket.Currency, StringComparer.Ordinal).ToArray(),
			rollup, knowledgeCutoff?.ToUniversalTime(), knowledgeCutoff.HasValue
				? CategoryReportAssignmentSelection.LatestRecordedAtOrBeforeCutoff : CategoryReportAssignmentSelection.CurrentPointer);
	}
}
