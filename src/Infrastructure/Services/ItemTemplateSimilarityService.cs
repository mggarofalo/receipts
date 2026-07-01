using Application.Interfaces.Services;
using Application.Queries.Core.ItemTemplate.GetSimilarItems;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace Infrastructure.Services;

public class ItemTemplateSimilarityService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	IEmbeddingService embeddingService,
	ILogger<ItemTemplateSimilarityService> logger) : IItemTemplateSimilarityService
{
	private const double TrigramWeight = 0.4;
	private const double VectorWeight = 0.6;

	public async Task<List<SimilarItemResult>> GetSimilarItemsAsync(string searchText, int limit, double threshold, bool useSemanticSearch, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();

		// Try to generate an embedding for the search text
		Vector? searchVector = null;
		if (useSemanticSearch && embeddingService.IsConfigured)
		{
			try
			{
				float[] embedding = await embeddingService.GenerateEmbeddingAsync(searchText, cancellationToken);
				if (embedding.Length > 0)
				{
					searchVector = new Vector(embedding);
				}
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Failed to generate search embedding, falling back to trigram-only");
			}
		}

		List<SimilarItemRow> rows;
		if (searchVector is not null)
		{
			rows = await RunHybridSearchAsync(context, searchText, searchVector, limit, threshold, cancellationToken);
		}
		else
		{
			rows = await RunTrigramOnlySearchAsync(context, searchText, limit, threshold, cancellationToken);
		}

		return [.. rows.Select(r => new SimilarItemResult
		{
			Name = r.name,
			Similarity = r.trigram_similarity,
			SemanticSimilarity = r.semantic_similarity,
			CombinedScore = r.combined_score,
			Source = r.source,
			DefaultCategory = r.default_category,
			DefaultSubcategory = r.default_subcategory,
			DefaultUnitPrice = r.default_unit_price,
			DefaultItemCode = r.default_item_code,
		})];
	}

	public async Task<List<CategoryRecommendation>> GetCategoryRecommendationsAsync(string description, int limit, CancellationToken cancellationToken)
	{
		// Get similar items (top 20) to aggregate category patterns
		List<SimilarItemResult> similar = await GetSimilarItemsAsync(description, 20, 0.1, true, cancellationToken);

		if (similar.Count == 0)
		{
			return [];
		}

		// Group by Category+Subcategory, weight by combined score
		List<SimilarItemResult> withCategories = similar
			.Where(s => !string.IsNullOrEmpty(s.DefaultCategory))
			.ToList();

		if (withCategories.Count == 0)
		{
			return [];
		}

		return [.. withCategories
			.GroupBy(s => new { s.DefaultCategory, s.DefaultSubcategory })
			.Select(g => new CategoryRecommendation
			{
				Category = g.Key.DefaultCategory!,
				Subcategory = g.Key.DefaultSubcategory,
				Confidence = g.Sum(s => s.CombinedScore) / withCategories.Count,
				OccurrenceCount = g.Count(),
			})
			.OrderByDescending(r => r.Confidence)
			.Take(limit)];
	}

	private static async Task<List<SimilarItemRow>> RunHybridSearchAsync(
		ApplicationDbContext context, string searchText, Vector searchVector, int limit, double threshold, CancellationToken cancellationToken)
	{
		string sql = """
			WITH template_matches AS (
			    SELECT
			        t."Id" AS entity_id,
			        t."Name" AS name,
			        similarity(t."Name", {0}) AS trigram_similarity,
			        'template' AS source,
			        t."DefaultCategory" AS default_category,
			        t."DefaultSubcategory" AS default_subcategory,
			        t."DefaultUnitPrice" AS default_unit_price,
			        t."DefaultItemCode" AS default_item_code,
			        'ItemTemplate' AS entity_type
			    FROM "library"."ItemTemplates" t
			    WHERE t."DeletedAt" IS NULL
			      AND similarity(t."Name", {0}) >= {1}
			),
			history_matches AS (
			    SELECT DISTINCT ON (ri."Description")
			        ri."Id" AS entity_id,
			        ri."Description" AS name,
			        similarity(ri."Description", {0}) AS trigram_similarity,
			        'history' AS source,
			        ri."Category" AS default_category,
			        ri."Subcategory" AS default_subcategory,
			        ri."UnitPrice" AS default_unit_price,
			        ri."ReceiptItemCode" AS default_item_code,
			        'ReceiptItem' AS entity_type
			    FROM "receipts"."ReceiptItems" ri
			    WHERE ri."DeletedAt" IS NULL
			      AND similarity(ri."Description", {0}) >= {1}
			    ORDER BY ri."Description", similarity(ri."Description", {0}) DESC
			),
			combined AS (
			    SELECT * FROM template_matches
			    UNION ALL
			    SELECT h.*
			    FROM history_matches h
			    WHERE NOT EXISTS (
			        SELECT 1 FROM template_matches t
			        WHERE LOWER(t.name) = LOWER(h.name)
			    )
			)
			SELECT
			    c.name,
			    c.trigram_similarity,
			    COALESCE(1.0 - (e."Embedding" <=> {2}::vector), 0) AS semantic_similarity,
			    (0.4 * c.trigram_similarity) + (0.6 * COALESCE(1.0 - (e."Embedding" <=> {2}::vector), 0)) AS combined_score,
			    c.source,
			    c.default_category,
			    c.default_subcategory,
			    c.default_unit_price,
			    c.default_item_code
			FROM combined c
			LEFT JOIN "matching"."ItemEmbeddings" e
			    ON e."EntityType" = c.entity_type AND e."EntityId" = c.entity_id
			ORDER BY combined_score DESC
			LIMIT {3}
			""";

		return await context.Database
			.SqlQueryRaw<SimilarItemRow>(sql, searchText, threshold, searchVector, limit)
			.ToListAsync(cancellationToken);
	}

	private static async Task<List<SimilarItemRow>> RunTrigramOnlySearchAsync(
		ApplicationDbContext context, string searchText, int limit, double threshold, CancellationToken cancellationToken)
	{
		string sql = """
			WITH template_matches AS (
			    SELECT
			        "Name" AS name,
			        similarity("Name", {0}) AS trigram_similarity,
			        CAST(NULL AS double precision) AS semantic_similarity,
			        similarity("Name", {0}) AS combined_score,
			        'template' AS source,
			        "DefaultCategory" AS default_category,
			        "DefaultSubcategory" AS default_subcategory,
			        "DefaultUnitPrice" AS default_unit_price,
			        "DefaultItemCode" AS default_item_code
			    FROM "library"."ItemTemplates"
			    WHERE "DeletedAt" IS NULL
			      AND similarity("Name", {0}) >= {1}
			),
			history_matches AS (
			    SELECT DISTINCT ON ("Description")
			        "Description" AS name,
			        similarity("Description", {0}) AS trigram_similarity,
			        CAST(NULL AS double precision) AS semantic_similarity,
			        similarity("Description", {0}) AS combined_score,
			        'history' AS source,
			        "Category" AS default_category,
			        "Subcategory" AS default_subcategory,
			        "UnitPrice" AS default_unit_price,
			        "ReceiptItemCode" AS default_item_code
			    FROM "receipts"."ReceiptItems"
			    WHERE "DeletedAt" IS NULL
			      AND similarity("Description", {0}) >= {1}
			    ORDER BY "Description", similarity("Description", {0}) DESC
			),
			combined AS (
			    SELECT * FROM template_matches
			    UNION ALL
			    SELECT h.*
			    FROM history_matches h
			    WHERE NOT EXISTS (
			        SELECT 1 FROM template_matches t
			        WHERE LOWER(t.name) = LOWER(h.name)
			    )
			)
			SELECT name, trigram_similarity, semantic_similarity, combined_score,
			       source, default_category, default_subcategory,
			       default_unit_price, default_item_code
			FROM combined
			ORDER BY combined_score DESC
			LIMIT {2}
			""";

		return await context.Database
			.SqlQueryRaw<SimilarItemRow>(sql, searchText, threshold, limit)
			.ToListAsync(cancellationToken);
	}

	private sealed class SimilarItemRow
	{
		public string name { get; set; } = string.Empty;
		public double trigram_similarity { get; set; }
		public double? semantic_similarity { get; set; }
		public double combined_score { get; set; }
		public string source { get; set; } = string.Empty;
		public string? default_category { get; set; }
		public string? default_subcategory { get; set; }
		public decimal? default_unit_price { get; set; }
		public string? default_item_code { get; set; }
	}
}
