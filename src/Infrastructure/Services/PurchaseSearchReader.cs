using Application.Interfaces.Services;
using Application.Models.Reports;
using Application.Models.Search;
using Domain.NormalizedDescriptions;
using Domain.Taxonomy;
using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class PurchaseSearchReader(IDbContextFactory<ApplicationDbContext> contextFactory) : IPurchaseSearchReader
{
	public async Task<PurchaseSearchResult> SearchAsync(string query, int offset, int limit,
		PurchaseSearchOptions options, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
		DateTimeOffset? cutoff = options.KnowledgeCutoff?.ToUniversalTime();
		Guid? head = options.CapturedCurrentRevisionId ?? (options.CategoryView == CategoryReportView.CurrentHierarchy
				? await context.TaxonomyHeads.AsNoTracking().Where(row => row.Id == 1)
						.Select(row => (Guid?)row.RevisionId).SingleOrDefaultAsync(cancellationToken) : null);
		if (options.CapturedCurrentRevisionId.HasValue
				&& !await context.TaxonomyRevisions.AsNoTracking().AnyAsync(row => row.Id == head, cancellationToken))
		{ throw new ArgumentException("capturedCurrentRevisionId does not identify a taxonomy revision", nameof(options.CapturedCurrentRevisionId)); }
		IQueryable<PurchaseClassificationEntity> assignments = context.Classifications.AsNoTracking().Where(assignment =>
				cutoff.HasValue
						? assignment.RecordedAt <= cutoff.Value
								&& !context.Classifications.Any(later => later.ReceiptItemId == assignment.ReceiptItemId
										&& later.RecordedAt <= cutoff.Value
										&& (later.RecordedAt > assignment.RecordedAt
												|| later.RecordedAt == assignment.RecordedAt && later.Sequence > assignment.Sequence))
						: context.CurrentClassifications.Any(pointer => pointer.ReceiptItemId == assignment.ReceiptItemId
								&& pointer.AssignmentId == assignment.Id));
		string pattern = LiteralPattern(query);
		var purchases =
			from item in context.ReceiptItems.IgnoreAutoIncludes().AsNoTracking()
			where item.DeletedAt == null
			join receipt in context.Receipts.IgnoreAutoIncludes().AsNoTracking().Where(r => r.DeletedAt == null)
				on item.ReceiptId equals receipt.Id
			join canonical in context.NormalizedDescriptions.AsNoTracking().Where(n => n.Status == NormalizedDescriptionStatus.Active
				|| (options.IncludeUnreviewed && n.Status == NormalizedDescriptionStatus.PendingReview))
				on item.NormalizedDescriptionId equals canonical.Id into canonicalGroup
			from canonical in canonicalGroup.DefaultIfEmpty()
			join assignment in assignments on item.Id equals assignment.ReceiptItemId into assignmentGroup
			from assignment in assignmentGroup.DefaultIfEmpty()
			where query == "" || EF.Functions.ILike(item.Description, pattern, @"\")
								|| (item.ReceiptItemCode != null && EF.Functions.ILike(item.ReceiptItemCode!, pattern, @"\"))
				|| EF.Functions.ILike(receipt.Location, pattern, @"\")
				|| (canonical != null
					&& item.NormalizationReviewState != NormalizationReviewState.Excluded
					&& (options.IncludeUnreviewed || item.NormalizationReviewState == NormalizationReviewState.Verified)
					&& (EF.Functions.ILike(canonical.CanonicalName, pattern, @"\")
						|| (canonical.DisplayLabel != null && EF.Functions.ILike(canonical.DisplayLabel, pattern, @"\"))))
			select new { Item = item, Receipt = receipt, Canonical = canonical, Assignment = assignment };

		if (options.CategoryId is Guid filterId)
		{
			IQueryable<TaxonomyNodeEntity> eligible = EligibleCategoryNodes(context, filterId,
					options.CategoryView == CategoryReportView.CurrentHierarchy ? head : null);
			bool historical = options.CategoryView == CategoryReportView.AsClassified;
			purchases = purchases.Where(row => row.Assignment != null
					&& row.Assignment.CategoryId.HasValue
					&& row.Assignment.LegacyResolution != "unresolved_path"
					&& !context.LegacyMigrationItems.Any(evidence => evidence.AssignmentId == row.Assignment.Id
							&& evidence.Resolution == "Unresolved")
					&& eligible.Any(node => node.CategoryId == row.Assignment.CategoryId
							&& (!historical || node.RevisionId == row.Assignment.TaxonomyRevisionId)));
		}

		if (options.NormalizedDescriptionId.HasValue)
		{
			purchases = purchases.Where(x => x.Canonical != null && x.Canonical.Id == options.NormalizedDescriptionId
				&& x.Item.NormalizationReviewState != NormalizationReviewState.Excluded
				&& (options.IncludeUnreviewed || x.Item.NormalizationReviewState == NormalizationReviewState.Verified));
		}

		if (!string.IsNullOrEmpty(options.Location))
		{
			string locationPattern = LiteralPattern(options.Location);
			purchases = purchases.Where(x => EF.Functions.ILike(x.Receipt.Location, locationPattern, @"\"));
		}
		if (options.StartDate.HasValue)
		{
			purchases = purchases.Where(x => x.Receipt.Date >= options.StartDate.Value);
		}
		if (options.EndDate.HasValue)
		{
			purchases = purchases.Where(x => x.Receipt.Date <= options.EndDate.Value);
		}
		if (options.MinUnitPrice.HasValue)
		{
			purchases = purchases.Where(x => x.Item.UnitPrice >= options.MinUnitPrice.Value);
		}
		if (options.MaxUnitPrice.HasValue)
		{
			purchases = purchases.Where(x => x.Item.UnitPrice <= options.MaxUnitPrice.Value);
		}

		// Search is a live read: concurrent writes may change the set between count and page.
		int total = await purchases.CountAsync(cancellationToken);
		bool descending = options.SortDirection == "desc";
		var ordered = options.SortBy switch
		{
			"date" => descending ? purchases.OrderByDescending(x => x.Receipt.Date) : purchases.OrderBy(x => x.Receipt.Date),
			"description" => descending ? purchases.OrderByDescending(x => x.Item.Description) : purchases.OrderBy(x => x.Item.Description),
			"location" => descending ? purchases.OrderByDescending(x => x.Receipt.Location) : purchases.OrderBy(x => x.Receipt.Location),
			"unitPrice" => descending ? purchases.OrderByDescending(x => x.Item.UnitPrice) : purchases.OrderBy(x => x.Item.UnitPrice),
			"quantity" => descending ? purchases.OrderByDescending(x => x.Item.Quantity) : purchases.OrderBy(x => x.Item.Quantity),
			"totalAmount" => descending ? purchases.OrderByDescending(x => x.Item.TotalAmount) : purchases.OrderBy(x => x.Item.TotalAmount),
			_ => throw new ArgumentOutOfRangeException(nameof(options), "Unsupported purchase sort column."),
		};
		var rows = await ordered
	.ThenBy(x => x.Receipt.Id).ThenBy(x => x.Item.Id)
	.Skip(offset).Take(limit)
	.Select(x => new
	{
		x.Item.Id,
		x.Item.ReceiptId,
		x.Receipt.Date,
		x.Receipt.Location,
		x.Item.Description,
		x.Item.ReceiptItemCode,
		NormalizedDescriptionId = x.Canonical == null || x.Item.NormalizationReviewState == NormalizationReviewState.Excluded || (!options.IncludeUnreviewed && x.Item.NormalizationReviewState != NormalizationReviewState.Verified) ? (Guid?)null : x.Canonical.Id,
		NormalizationReviewState = x.Item.NormalizationReviewState,
		NormalizedDescription = x.Canonical == null || x.Item.NormalizationReviewState == NormalizationReviewState.Excluded || (!options.IncludeUnreviewed && x.Item.NormalizationReviewState != NormalizationReviewState.Verified) ? null
			: string.IsNullOrEmpty(x.Canonical.DisplayLabel) ? x.Canonical.CanonicalName : x.Canonical.DisplayLabel,
		x.Item.Quantity,
		x.Item.UnitPrice,
		x.Item.UnitPriceCurrency,
		x.Item.TotalAmount,
		x.Item.TotalAmountCurrency,
		AssignmentCategoryId = x.Assignment == null ? (Guid?)null : x.Assignment.CategoryId,
		AssignmentRevisionId = x.Assignment == null ? (Guid?)null : x.Assignment.TaxonomyRevisionId,
		HasAssignment = x.Assignment != null,
		IsUnresolvedLegacy = x.Assignment != null && (x.Assignment.LegacyResolution == "unresolved_path"
						|| context.LegacyMigrationItems.Any(evidence => evidence.AssignmentId == x.Assignment.Id
								&& evidence.Resolution == "Unresolved")),
		BeforeMigrationHistory = cutoff.HasValue && x.Assignment == null
						&& context.Classifications.Any(history => history.ReceiptItemId == x.Item.Id
								&& history.Source == "legacy_import" && history.RecordedAt > cutoff.Value),
	}).ToListAsync(cancellationToken);

		Guid[] pageRevisions = options.CategoryView == CategoryReportView.CurrentHierarchy
				? head.HasValue ? [head.Value] : []
				: rows.Where(row => row.AssignmentCategoryId.HasValue && row.AssignmentRevisionId.HasValue)
						.Select(row => row.AssignmentRevisionId!.Value).Distinct().ToArray();
		var nodes = await context.TaxonomyNodes.AsNoTracking().Where(node => pageRevisions.Contains(node.RevisionId))
				.Select(node => new { node.RevisionId, node.CategoryId, node.ParentId, node.Name, node.Archived })
				.ToListAsync(cancellationToken);
		CategoryReportNode[] snapshotNodes = nodes.Select(node => new CategoryReportNode(node.RevisionId,
				new TaxonomyNode(node.CategoryId, node.ParentId, node.Name, null, node.Archived))).ToArray();

		return new PurchaseSearchResult(rows.Select(x =>
		{
			CategoryReportBucket bucket = Resolve(x.AssignmentCategoryId, x.AssignmentRevisionId,
							x.HasAssignment, x.IsUnresolvedLegacy, x.BeforeMigrationHistory);
			return new PurchaseSearchItem(
		x.Id, x.ReceiptId, x.Date, x.Location, x.Description, x.ReceiptItemCode,
		x.NormalizedDescriptionId, x.NormalizedDescription, x.Quantity,
		x.UnitPrice, x.UnitPriceCurrency.ToString(), x.TotalAmount, x.TotalAmountCurrency.ToString(),
							x.NormalizationReviewState == NormalizationReviewState.Verified ? "verified" : x.NormalizationReviewState == NormalizationReviewState.Excluded ? "excluded" : "needsReview")
			{
				CategoryId = bucket.CategoryId,
				PathRevisionId = bucket.PathRevisionId,
				CategoryState = bucket.State,
				CategoryPath = bucket.Path,
			};
		}).ToList(), total, offset, limit)
		{
			CategoryView = options.CategoryView,
			CurrentRevisionId = options.CategoryView == CategoryReportView.CurrentHierarchy ? head : null,
			KnowledgeCutoff = cutoff,
			AssignmentSelection = cutoff.HasValue ? CategoryReportAssignmentSelection.LatestRecordedAtOrBeforeCutoff
						: CategoryReportAssignmentSelection.CurrentPointer,
		};

		CategoryReportBucket Resolve(Guid? categoryId, Guid? revisionId, bool hasAssignment,
				bool unresolvedLegacy, bool beforeMigration)
		{
			CategoryReportState state = !hasAssignment ? beforeMigration ? CategoryReportState.BeforeMigrationHistory
							: CategoryReportState.MissingAssignment
					: unresolvedLegacy ? CategoryReportState.UnresolvedLegacy
					: categoryId.HasValue ? CategoryReportState.Classified : CategoryReportState.Unclassified;
			return CategoryReportProjection.Project(
					[new CategoryReportFact(categoryId, revisionId, state, "", 0, 1)], snapshotNodes,
					options.CategoryView, head).Buckets.Single();
		}
	}

	private static string LiteralPattern(string value)
		=> "%" + value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

	// The first walk validates the filter node's path to a root in each immutable revision.
	// The second walks its subtree. Both use stable IDs and guard against corrupt cycles.
	// Keeping this as a composable relation lets PostgreSQL filter before count and paging.
	private static IQueryable<TaxonomyNodeEntity> EligibleCategoryNodes(
			ApplicationDbContext context, Guid categoryId, Guid? revisionId)
			=> context.TaxonomyNodes.FromSqlInterpolated($"""
                        WITH RECURSIVE ancestors AS (
                            SELECT n."RevisionId", n."CategoryId", n."ParentId",
                                   ARRAY[n."CategoryId"] AS path
                            FROM taxonomy."Nodes" AS n
                            WHERE n."CategoryId" = {categoryId}
                              AND ({revisionId}::uuid IS NULL OR n."RevisionId" = {revisionId})
                            UNION ALL
                            SELECT parent."RevisionId", parent."CategoryId", parent."ParentId",
                                   ancestors.path || parent."CategoryId"
                            FROM ancestors
                            JOIN taxonomy."Nodes" AS parent
                              ON parent."RevisionId" = ancestors."RevisionId"
                             AND parent."CategoryId" = ancestors."ParentId"
                            WHERE NOT parent."CategoryId" = ANY(ancestors.path)
                        ), valid_revisions AS (
                            SELECT DISTINCT "RevisionId" FROM ancestors WHERE "ParentId" IS NULL
                        ), descendants AS (
                            SELECT n."RevisionId", n."CategoryId", ARRAY[n."CategoryId"] AS path
                            FROM taxonomy."Nodes" AS n
                            JOIN valid_revisions AS valid ON valid."RevisionId" = n."RevisionId"
                            WHERE n."CategoryId" = {categoryId}
                            UNION ALL
                            SELECT child."RevisionId", child."CategoryId",
                                   descendants.path || child."CategoryId"
                            FROM descendants
                            JOIN taxonomy."Nodes" AS child
                              ON child."RevisionId" = descendants."RevisionId"
                             AND child."ParentId" = descendants."CategoryId"
                            WHERE NOT child."CategoryId" = ANY(descendants.path)
                        )
                        SELECT node.* FROM descendants
                        JOIN taxonomy."Nodes" AS node
                          ON node."RevisionId" = descendants."RevisionId"
                         AND node."CategoryId" = descendants."CategoryId"
                        """).AsNoTracking();
}
