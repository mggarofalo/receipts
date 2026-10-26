using Application.Models.Reports;
using Domain.Taxonomy;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>
/// Shared reporting foundation. Callers supply the complete authorized item scope, including
/// existing location/product/price filters. Receipt dates here are purchase dates, never recorded-at.
/// The query and context must have the same owner. Capture a repeatable-read transaction at the
/// endpoint boundary when the response requires a single consistent purchase/assignment snapshot.
/// </summary>
public static class CategoryReportReader
{
	public static async Task<CategoryReportResult> ReadAsync(ApplicationDbContext context,
		IQueryable<ReceiptItemEntity> scopedItems, DateOnly startDate, DateOnly endDate,
		CategoryReportView view, CategoryReportRollup rollup = CategoryReportRollup.AssignedCategory,
		Guid? categoryFilter = null, CancellationToken cancellationToken = default,
		DateTimeOffset? knowledgeCutoff = null, Guid? capturedCurrentRevisionId = null, string? granularity = null)
	{
		if (startDate > endDate) { throw new ArgumentException("Start date must not follow end date."); }
		if (!Enum.IsDefined(view)) { throw new ArgumentOutOfRangeException(nameof(view)); }
		if (!Enum.IsDefined(rollup)) { throw new ArgumentOutOfRangeException(nameof(rollup)); }
		if (capturedCurrentRevisionId.HasValue && view != CategoryReportView.CurrentHierarchy)
		{ throw new ArgumentException("A captured current revision requires the current-hierarchy view.", nameof(capturedCurrentRevisionId)); }
		knowledgeCutoff = knowledgeCutoff?.ToUniversalTime();
		Guid? head = capturedCurrentRevisionId ?? (view == CategoryReportView.CurrentHierarchy
			? await context.TaxonomyHeads.AsNoTracking().Where(row => row.Id == 1)
				.Select(row => (Guid?)row.RevisionId).SingleOrDefaultAsync(cancellationToken) : null);
		var selectedAssignments = context.Classifications.AsNoTracking().Where(assignment =>
			knowledgeCutoff.HasValue
				? assignment.RecordedAt <= knowledgeCutoff.Value
					&& !context.Classifications.Any(later => later.ReceiptItemId == assignment.ReceiptItemId
						&& later.RecordedAt <= knowledgeCutoff.Value
						&& (later.RecordedAt > assignment.RecordedAt
							|| later.RecordedAt == assignment.RecordedAt && later.Sequence > assignment.Sequence))
				: context.CurrentClassifications.Any(pointer => pointer.ReceiptItemId == assignment.ReceiptItemId
					&& pointer.AssignmentId == assignment.Id));
		var purchases = from item in scopedItems.AsNoTracking().IgnoreAutoIncludes()
						join receipt in context.Receipts.AsNoTracking().IgnoreAutoIncludes() on item.ReceiptId equals receipt.Id
						where item.DeletedAt == null && receipt.DeletedAt == null && receipt.Date >= startDate && receipt.Date <= endDate
						join assignment in selectedAssignments on item.Id equals assignment.ReceiptItemId into assignments
						from assignment in assignments.DefaultIfEmpty()
						select new
						{
							Year = granularity == null ? 0 : receipt.Date.Year,
							Month = granularity == null || granularity == "yearly" ? 1 : granularity == "quarterly" ? ((receipt.Date.Month - 1) / 3) * 3 + 1 : receipt.Date.Month,
							Day = granularity == "daily" ? receipt.Date.Day : 1,
							item.TotalAmount,
							item.TotalAmountCurrency,
							CategoryId = assignment == null ? (Guid?)null : assignment.CategoryId,
							RevisionId = assignment == null ? (Guid?)null : assignment.TaxonomyRevisionId,
							State = assignment == null ? knowledgeCutoff.HasValue
								&& context.Classifications.Any(history => history.ReceiptItemId == item.Id
									&& history.Source == "legacy_import" && history.RecordedAt > knowledgeCutoff.Value)
									? CategoryReportState.BeforeMigrationHistory : CategoryReportState.MissingAssignment
								: assignment.LegacyResolution == "unresolved_path"
									|| context.LegacyMigrationItems.Any(evidence => evidence.AssignmentId == assignment.Id
										&& evidence.Resolution == "Unresolved") ? CategoryReportState.UnresolvedLegacy
								: assignment.CategoryId == null ? CategoryReportState.Unclassified : CategoryReportState.Classified
						};
		var groups = await purchases.GroupBy(row => new { row.Year, row.Month, row.Day, row.CategoryId, row.RevisionId, row.State, row.TotalAmountCurrency })
			.Select(group => new { group.Key, Amount = group.Sum(row => row.TotalAmount), Count = group.Count() })
			.ToListAsync(cancellationToken);
		Guid[] revisions = view == CategoryReportView.CurrentHierarchy
			? head.HasValue ? [head.Value] : []
			: groups.Where(row => row.Key.RevisionId.HasValue && row.Key.CategoryId.HasValue)
				.Select(row => row.Key.RevisionId!.Value).Distinct().ToArray();
		var nodes = await context.TaxonomyNodes.AsNoTracking().Where(row => revisions.Contains(row.RevisionId))
			.Select(row => new { row.RevisionId, row.CategoryId, row.ParentId, row.Name, row.Archived }).ToListAsync(cancellationToken);
		var snapshotNodes = nodes.Select(row => new CategoryReportNode(row.RevisionId,
			new TaxonomyNode(row.CategoryId, row.ParentId, row.Name, null, row.Archived))).ToArray();
		CategoryReportResult Project(IEnumerable<CategoryReportFact> facts) => CategoryReportProjection.Project(
			facts, snapshotNodes, view, head, rollup, categoryFilter, knowledgeCutoff);
		var facts = groups.Select(row => new
		{
			row.Key.Year,
			row.Key.Month,
			row.Key.Day,
			Fact = new CategoryReportFact(row.Key.CategoryId, row.Key.RevisionId, row.Key.State,
				row.Key.TotalAmountCurrency.ToString(), row.Amount, row.Count)
		}).ToArray();
		var snapshot = Project(facts.Select(row => row.Fact));
		return granularity == null ? snapshot : snapshot with
		{
			Periods = facts.GroupBy(row => new { row.Year, row.Month, row.Day }).ToDictionary(
				group => granularity switch
				{
					"daily" => $"{group.Key.Year:D4}-{group.Key.Month:D2}-{group.Key.Day:D2}",
					"quarterly" => $"{group.Key.Year} Q{(group.Key.Month - 1) / 3 + 1}",
					"yearly" => group.Key.Year.ToString(),
					_ => $"{group.Key.Year:D4}-{group.Key.Month:D2}"
				},
				group => Project(group.Select(row => row.Fact)).Buckets)
		};
	}
}
