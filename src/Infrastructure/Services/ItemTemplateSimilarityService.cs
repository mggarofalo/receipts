using System.Globalization;
using Application.Interfaces.Services;
using Application.Normalization;
using Application.Queries.Core.ItemTemplate.GetSimilarItems;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;

namespace Infrastructure.Services;

public class ItemTemplateSimilarityService(
	IDbContextFactory<ApplicationDbContext> contextFactory,
	IEmbeddingService embeddingService,
	ILogger<ItemTemplateSimilarityService> logger) : IItemTemplateSimilarityService
{
	/// <summary>
	/// How many template and receipt-item vectors the hybrid search reads, nearest first (RECEIPTS-1098).
	/// Every purchase has its own vector, so a frequent description repeats; the rows collapse to names
	/// afterwards. The budget sits well above the largest repeat seen locally (a few hundred rows), so it
	/// always spans many names, and reading it through HNSW still takes milliseconds.
	/// </summary>
	internal const int SemanticNeighbourRows = 1000;

	private const double TrigramWeight = 0.4;
	private const double VectorWeight = 0.6;

	public async Task<List<SimilarItemResult>> GetSimilarItemsAsync(string searchText, int limit, double threshold, bool useSemanticSearch, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
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
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Failed to generate search embedding, falling back to trigram-only");
			}
		}

		cancellationToken.ThrowIfCancellationRequested();
		// `%` reads its threshold from pg_trgm.similarity_threshold. Unlike similarity() >= t it can be
		// served by the GIN trigram indexes, and SET LOCAL keeps the setting to this transaction.
		await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);
		await SetThresholdAsync(context, threshold, cancellationToken);
		(string sql, NpgsqlParameter[] parameters) = Query(searchText, searchVector, limit);
		IQueryable<SimilarItemRow> query = context.Database.SqlQueryRaw<SimilarItemRow>(sql, parameters);
		List<SimilarItemRow> rows = searchVector is null
			? await query.ToListAsync(cancellationToken)
			: await NearestNeighbourSearch.ToListAsync(context.Database, query, logger, cancellationToken);
		await transaction.CommitAsync(cancellationToken);

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
			AdmittedBySemanticSearchOnly = r.semantic_only,
		})];
	}

	internal static Task SetThresholdAsync(ApplicationDbContext context, double threshold, CancellationToken cancellationToken) =>
		context.Database.ExecuteSqlRawAsync("SELECT set_config('pg_trgm.similarity_threshold', @threshold, true)",
			[new NpgsqlParameter("threshold", threshold.ToString(CultureInfo.InvariantCulture))], cancellationToken);

	/// <summary>
	/// The search SQL and its parameters; run it after <see cref="SetThresholdAsync"/> in the same transaction.
	/// With a vector it adds the semantic neighbours to the trigram matches before scoring (RECEIPTS-1098).
	/// </summary>
	internal static (string Sql, NpgsqlParameter[] Parameters) Query(string searchText, Vector? searchVector, int limit)
	{
		List<NpgsqlParameter> parameters = [new("q", searchText), new("limit", limit)];
		string knn = "";
		string templateNeighbours = "";
		string historyNeighbours = "";
		string templateVector = "CAST(NULL AS double precision)";
		string historyVector = "CAST(NULL AS double precision)";
		if (searchVector is not null)
		{
			parameters.Add(new("vector", searchVector));
			parameters.Add(new("fingerprint", OnnxEmbeddingService.EmbeddingSpaceFingerprint));
			parameters.Add(new("k", SemanticNeighbourRows));
			parameters.Add(new("floor", ReceiptSuggestionEvidence.SemanticFloor));
			// The kNN rule every vector query follows (NearestNeighbourSearch, RECEIPTS-1097). A vector
			// counts only while it was built from the row's current text. A neighbour scores at any
			// similarity but is admitted on its own only at the floor the handler keeps, so neighbours
			// it would drop cannot take the places of trigram matches under the LIMIT.
			knn = """
				knn AS (
				    SELECT e."EntityType" AS entity_type, e."EntityId" AS entity_id, e."EntityText" AS entity_text,
				           1 - (e."Embedding" <=> @vector) AS similarity
				    FROM "matching"."ItemEmbeddings" e
				    WHERE e."EntityType" IN ('ItemTemplate', 'ReceiptItem') AND e."ModelVersion" = @fingerprint
				    ORDER BY e."Embedding" <=> @vector, e."Id"
				    LIMIT @k
				),
				knn_templates AS (
				    SELECT t."Id", max(k.similarity) AS similarity
				    FROM knn k JOIN "library"."ItemTemplates" t ON t."Id" = k.entity_id AND t."Name" = k.entity_text
				    WHERE k.entity_type = 'ItemTemplate' AND t."DeletedAt" IS NULL
				    GROUP BY t."Id"
				),
				knn_history AS (
				    SELECT ri."Description" AS name, max(k.similarity) AS similarity
				    FROM knn k JOIN "receipts"."ReceiptItems" ri ON ri."Id" = k.entity_id AND ri."Description" = k.entity_text
				    WHERE k.entity_type = 'ReceiptItem' AND ri."DeletedAt" IS NULL
				    GROUP BY ri."Description"
				),
				""";
			templateNeighbours = """
				    UNION
				    SELECT "Id" FROM knn_templates WHERE similarity >= @floor
				""";
			historyNeighbours = """
				    UNION
				    SELECT ri."Id", ri."Description", ri."ReceiptId", ri."UnitPrice", ri."ReceiptItemCode"
				    FROM knn_history k JOIN "receipts"."ReceiptItems" ri ON ri."Description" = k.name
				    WHERE k.similarity >= @floor AND ri."DeletedAt" IS NULL
				""";
			// A neighbour's similarity, else the row's own current vector; null without one.
			templateVector = """
				COALESCE((SELECT similarity FROM knn_templates k WHERE k."Id" = t."Id"),
				         (SELECT 1 - (e."Embedding" <=> @vector) FROM "matching"."ItemEmbeddings" e
				          WHERE e."EntityType" = 'ItemTemplate' AND e."EntityId" = t."Id"
				            AND e."EntityText" = t."Name" AND e."ModelVersion" = @fingerprint))
				""";
			historyVector = """
				COALESCE((SELECT similarity FROM knn_history k WHERE k.name = h.name),
				         (SELECT 1 - (e."Embedding" <=> @vector) FROM "matching"."ItemEmbeddings" e
				          WHERE e."EntityType" = 'ReceiptItem' AND e."EntityId" = h.entity_id
				            AND e."EntityText" = h.name AND e."ModelVersion" = @fingerprint))
				""";
		}

		string sql = $"""
			WITH {knn}
			template_ids AS (
			    SELECT "Id" FROM "library"."ItemTemplates" WHERE "DeletedAt" IS NULL AND "Name" % @q
			{templateNeighbours}
			),
			history_rows AS (
			    SELECT ri."Id", ri."Description", ri."ReceiptId", ri."UnitPrice", ri."ReceiptItemCode"
			    FROM "receipts"."ReceiptItems" ri
			    WHERE ri."DeletedAt" IS NULL AND ri."Description" % @q
			{historyNeighbours}
			),
			-- One row per description, so its price and code come from the latest purchase, then the latest Id.
			history AS (
			    SELECT DISTINCT ON (h."Description")
			        h."Id" AS entity_id, h."Description" AS name, h."UnitPrice", h."ReceiptItemCode"
			    FROM history_rows h
			    JOIN "receipts"."Receipts" r ON r."Id" = h."ReceiptId" AND r."DeletedAt" IS NULL
			    ORDER BY h."Description", r."Date" DESC, h."Id" DESC
			),
			candidates AS (
			    SELECT t."Name" AS name, similarity(t."Name", @q) AS trigram_similarity,
			           {templateVector} AS semantic_similarity,
			           NOT (t."Name" % @q) AS semantic_only,
			           'template' AS source, t."DefaultCategory" AS default_category,
			           t."DefaultSubcategory" AS default_subcategory, t."DefaultUnitPrice" AS default_unit_price,
			           t."DefaultItemCode" AS default_item_code
			    FROM template_ids i JOIN "library"."ItemTemplates" t ON t."Id" = i."Id"
			    UNION ALL
			    SELECT h.name, similarity(h.name, @q), {historyVector}, NOT (h.name % @q),
			           'history', CAST(NULL AS text), CAST(NULL AS text), h."UnitPrice", h."ReceiptItemCode"
			    FROM history h
			    WHERE NOT EXISTS (
			        SELECT 1 FROM template_ids i JOIN "library"."ItemTemplates" t ON t."Id" = i."Id"
			        WHERE LOWER(t."Name") = LOWER(h.name))
			)
			-- A row without a current vector scores by trigram alone, so partial coverage cannot reorder results.
			SELECT name, trigram_similarity, semantic_similarity, semantic_only,
			       CASE WHEN semantic_similarity IS NULL THEN trigram_similarity
			            ELSE {TrigramWeight.ToString(CultureInfo.InvariantCulture)} * trigram_similarity
			               + {VectorWeight.ToString(CultureInfo.InvariantCulture)} * semantic_similarity END AS combined_score,
			       source, default_category, default_subcategory, default_unit_price, default_item_code
			FROM candidates
			ORDER BY combined_score DESC, name
			LIMIT @limit
			""";
		return (sql, [.. parameters]);
	}

	private sealed class SimilarItemRow
	{
		public string name { get; set; } = string.Empty;
		public double trigram_similarity { get; set; }
		public double? semantic_similarity { get; set; }
		public bool semantic_only { get; set; }
		public double combined_score { get; set; }
		public string source { get; set; } = string.Empty;
		public string? default_category { get; set; }
		public string? default_subcategory { get; set; }
		public decimal? default_unit_price { get; set; }
		public string? default_item_code { get; set; }
	}
}
