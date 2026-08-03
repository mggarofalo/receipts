using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.ItemTemplate.GetHistoryCandidates;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

/// <summary>
/// Aggregates active receipt-item history into "template candidates": recurring descriptions
/// that do not yet have a matching item template. The aggregation spans the receipts and
/// library schemas, so it is expressed as raw SQL rather than LINQ.
/// </summary>
public class ItemTemplateHistoryCandidateService(IDbContextFactory<ApplicationDbContext> contextFactory)
	: IItemTemplateHistoryCandidateService
{
	/// <summary>
	/// Active (non-deleted) receipt items joined to their receipts for the purchase date, grouped
	/// case-insensitively by description, filtered to groups that meet the occurrence floor and
	/// have no matching item template. Ends without a trailing comma so callers can append either
	/// a count projection or further CTEs.
	/// </summary>
	private const string CandidateCteSql = """
		WITH active_items AS (
		    SELECT
		        LOWER(ri."Description") AS description_key,
		        ri."Description"        AS description,
		        ri."Category"           AS category,
		        ri."Subcategory"        AS subcategory,
		        ri."UnitPrice"          AS unit_price,
		        ri."ReceiptItemCode"    AS item_code,
		        r."Date"                AS purchased_at,
		        ri."Id"                 AS item_id
		    FROM "receipts"."ReceiptItems" ri
		    INNER JOIN "receipts"."Receipts" r ON r."Id" = ri."ReceiptId"
		    WHERE ri."DeletedAt" IS NULL
		      AND r."DeletedAt" IS NULL
		),
		grouped AS (
		    SELECT
		        description_key,
		        COUNT(*)::int        AS occurrence_count,
		        MAX(purchased_at)    AS last_purchased_at
		    FROM active_items
		    GROUP BY description_key
		    HAVING COUNT(*) >= {0}
		),
		candidates AS (
		    SELECT g.*
		    FROM grouped g
		    WHERE NOT EXISTS (
		        SELECT 1
		        FROM "library"."ItemTemplates" t
		        WHERE t."DeletedAt" IS NULL
		          AND LOWER(t."Name") = g.description_key
		    )
		)
		""";

	public async Task<PagedResult<ItemTemplateHistoryCandidate>> GetHistoryCandidatesAsync(int offset, int limit, int minCount, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();

		string countSql = CandidateCteSql + """

			SELECT COUNT(*)::int AS "Value" FROM candidates
			""";

		int total = await context.Database
			.SqlQueryRaw<int>(countSql, minCount)
			.SingleAsync(cancellationToken);

		if (total == 0 || offset >= total)
		{
			return new PagedResult<ItemTemplateHistoryCandidate>([], total, offset, limit);
		}

		string pageSql = CandidateCteSql + """
			,
			latest AS (
			    SELECT DISTINCT ON (description_key)
			        description_key,
			        description AS name,
			        unit_price
			    FROM active_items
			    ORDER BY description_key, purchased_at DESC, item_id DESC
			),
			category_counts AS (
			    SELECT description_key, category, subcategory, COUNT(*) AS pair_count
			    FROM active_items
			    WHERE category <> ''
			    GROUP BY description_key, category, subcategory
			),
			top_category AS (
			    SELECT DISTINCT ON (description_key)
			        description_key, category, subcategory
			    FROM category_counts
			    ORDER BY description_key, pair_count DESC, category ASC, subcategory ASC NULLS LAST
			),
			item_code_counts AS (
			    SELECT description_key, item_code, COUNT(*) AS code_count
			    FROM active_items
			    WHERE item_code IS NOT NULL AND item_code <> ''
			    GROUP BY description_key, item_code
			),
			top_item_code AS (
			    SELECT DISTINCT ON (description_key)
			        description_key, item_code
			    FROM item_code_counts
			    ORDER BY description_key, code_count DESC, item_code ASC
			)
			SELECT
			    l.name                       AS name,
			    c.occurrence_count           AS occurrence_count,
			    c.last_purchased_at          AS last_purchased_at,
			    NULLIF(tc.category, '')      AS suggested_category,
			    NULLIF(tc.subcategory, '')   AS suggested_subcategory,
			    l.unit_price                 AS suggested_unit_price,
			    NULLIF(ic.item_code, '')     AS suggested_item_code
			FROM candidates c
			INNER JOIN latest l ON l.description_key = c.description_key
			LEFT JOIN top_category tc ON tc.description_key = c.description_key
			LEFT JOIN top_item_code ic ON ic.description_key = c.description_key
			ORDER BY c.occurrence_count DESC, l.name ASC
			OFFSET {1} LIMIT {2}
			""";

		List<HistoryCandidateRow> rows = await context.Database
			.SqlQueryRaw<HistoryCandidateRow>(pageSql, minCount, offset, limit)
			.ToListAsync(cancellationToken);

		List<ItemTemplateHistoryCandidate> data = [.. rows.Select(r => new ItemTemplateHistoryCandidate
		{
			Name = r.name,
			OccurrenceCount = r.occurrence_count,
			LastPurchasedAt = r.last_purchased_at,
			SuggestedCategory = r.suggested_category,
			SuggestedSubcategory = r.suggested_subcategory,
			SuggestedUnitPrice = r.suggested_unit_price,
			SuggestedItemCode = r.suggested_item_code,
		})];

		return new PagedResult<ItemTemplateHistoryCandidate>(data, total, offset, limit);
	}

	private sealed class HistoryCandidateRow
	{
		public string name { get; set; } = string.Empty;
		public int occurrence_count { get; set; }
		public DateOnly last_purchased_at { get; set; }
		public string? suggested_category { get; set; }
		public string? suggested_subcategory { get; set; }
		public decimal? suggested_unit_price { get; set; }
		public string? suggested_item_code { get; set; }
	}
}
