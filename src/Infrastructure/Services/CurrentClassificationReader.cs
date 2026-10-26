using Domain.Taxonomy;
using Infrastructure.Entities.Core;
using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>Enriches a response page using a bounded number of reads, independent of item count.</summary>
internal static class CurrentClassificationReader
{
	private sealed record AssignmentRow(Guid Id, Guid ReceiptItemId, Guid? CategoryId, Guid TaxonomyRevisionId);

	public static async Task PopulateAsync(ApplicationDbContext context, IReadOnlyCollection<ReceiptItemEntity> items,
		CancellationToken cancellationToken)
	{
		if (items.Count == 0) { return; }
		Guid[] itemIds = items.Select(item => item.Id).ToArray();
		List<AssignmentRow> assignments = await (
			from current in context.CurrentClassifications.AsNoTracking()
			join assignment in context.Classifications.AsNoTracking()
				on new { current.ReceiptItemId, Id = current.AssignmentId }
				equals new { assignment.ReceiptItemId, assignment.Id }
			where itemIds.Contains(current.ReceiptItemId)
			select new AssignmentRow(assignment.Id, assignment.ReceiptItemId, assignment.CategoryId, assignment.TaxonomyRevisionId)).ToListAsync(cancellationToken);
		Guid[] revisions = assignments.Where(assignment => assignment.CategoryId.HasValue)
			.Select(assignment => assignment.TaxonomyRevisionId).Distinct().ToArray();
		List<TaxonomyNodeEntity> nodes = revisions.Length == 0 ? [] : await context.TaxonomyNodes.AsNoTracking()
			.Where(node => revisions.Contains(node.RevisionId)).ToListAsync(cancellationToken);
		Dictionary<(Guid, Guid), TaxonomyNodeEntity> nodesById = nodes.ToDictionary(node => (node.RevisionId, node.CategoryId));
		Dictionary<Guid, CurrentClassification> byItem = [];
		foreach (AssignmentRow assignment in assignments)
		{
			List<TaxonomyNode> path = [];
			Guid? cursor = assignment.CategoryId;
			while (cursor is Guid categoryId)
			{
				TaxonomyNodeEntity node = nodesById[(assignment.TaxonomyRevisionId, categoryId)];
				path.Add(new(node.CategoryId, node.ParentId, node.Name, node.Definition, node.Archived));
				cursor = node.ParentId;
			}
			path.Reverse();
			byItem.Add(assignment.ReceiptItemId, new(assignment.Id, assignment.CategoryId, assignment.TaxonomyRevisionId, path.AsReadOnly()));
		}
		foreach (ReceiptItemEntity item in items) { item.CurrentClassification = byItem.GetValueOrDefault(item.Id); }
	}
}
