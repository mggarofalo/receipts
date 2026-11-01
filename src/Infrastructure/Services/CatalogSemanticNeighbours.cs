using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Pgvector;

namespace Infrastructure.Services;

/// <summary>
/// Semantic similarity between receipt text and active catalog names (RECEIPTS-1096). Every name has a
/// vector of its canonical name; a name with a display label also has a vector of the label, kept in
/// <c>matching."ItemEmbeddings"</c> under <see cref="LabelEntityType"/>. A name's similarity is the closer
/// of the two, so the label adds matches without losing those the canonical name won. A label vector
/// counts only while it was built from the current label and in the current embedding space.
/// </summary>
internal static class CatalogSemanticNeighbours
{
	internal const string LabelEntityType = "NormalizedDescriptionLabel";

	/// <summary>
	/// The <paramref name="limit"/> nearest active names. Run it through <see cref="NearestNeighbourSearch"/>
	/// for the canonical half. The label half is an exact scan over at most one row per name: the
	/// materialized CTE filters to label rows before ordering, so the ItemEmbeddings HNSW index, which
	/// mostly holds receipt items, can never spend the iterative scan's tuple budget on other rows.
	/// </summary>
	internal static IQueryable<CatalogNeighbour> Nearest(DatabaseFacade database, Vector query, int limit)
	{
		string fingerprint = OnnxEmbeddingService.EmbeddingSpaceFingerprint;
		return database.SqlQuery<CatalogNeighbour>($"""
			WITH labels AS MATERIALIZED (
				SELECT d."Id", e."Embedding" FROM matching."ItemEmbeddings" e
				JOIN matching."NormalizedDescriptions" d ON d."Id" = e."EntityId"
				WHERE e."EntityType" = {LabelEntityType} AND e."ModelVersion" = {fingerprint}
					AND d."Status" = 'Active' AND e."EntityText" = d."DisplayLabel")
			SELECT n."Id", max(n."Similarity") AS "Similarity" FROM (
				(SELECT "Id", 1 - ("Embedding" <=> {query}::vector) AS "Similarity" FROM matching."NormalizedDescriptions"
				WHERE "Status" = 'Active' AND "Embedding" IS NOT NULL AND "EmbeddingModelVersion" = {fingerprint}
				ORDER BY "Embedding" <=> {query}::vector, "Id" LIMIT {limit})
				UNION ALL
				(SELECT "Id", 1 - ("Embedding" <=> {query}::vector) FROM labels
				ORDER BY "Embedding" <=> {query}::vector, "Id" LIMIT {limit})) n
			GROUP BY n."Id" ORDER BY max(n."Similarity") DESC, n."Id" LIMIT {limit}
			""");
	}

	/// <summary>One name's similarity by the same rule, whatever its status; empty when it has no current vector.</summary>
	internal static IQueryable<double> SimilarityOf(DatabaseFacade database, Vector query, Guid id)
	{
		string fingerprint = OnnxEmbeddingService.EmbeddingSpaceFingerprint;
		return database.SqlQuery<double>($"""
			SELECT max(s."Value") AS "Value" FROM (
				SELECT 1 - ("Embedding" <=> {query}::vector) AS "Value" FROM matching."NormalizedDescriptions"
				WHERE "Id" = {id} AND "Embedding" IS NOT NULL AND "EmbeddingModelVersion" = {fingerprint}
				UNION ALL
				SELECT 1 - (e."Embedding" <=> {query}::vector) FROM matching."ItemEmbeddings" e
				JOIN matching."NormalizedDescriptions" d ON d."Id" = e."EntityId"
				WHERE e."EntityType" = {LabelEntityType} AND e."EntityId" = {id} AND e."ModelVersion" = {fingerprint}
					AND e."EntityText" = d."DisplayLabel") s
			HAVING count(*) > 0
			""");
	}
}

internal sealed class CatalogNeighbour
{
	public Guid Id { get; set; }
	public double Similarity { get; set; }
}
