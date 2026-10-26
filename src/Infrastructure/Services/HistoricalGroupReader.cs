using System.Text.Json;
using Application.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.Services;

/// <summary>Reads live exact-source groups. No classification or provider work is performed.</summary>
public sealed class HistoricalGroupReader(IDbContextFactory<ApplicationDbContext> factory) : IHistoricalGroupReader
{
	public async Task<HistoricalGroupPage> BrowseAsync(HistoricalGroupBrowseRequest request,
		CancellationToken cancellationToken = default)
	{
		if (request.Offset < 0 || request.Limit is < 1 or > 25 ||
			request.Search is { Length: > 200 } || request.Search?.Contains('\0') == true)
		{ throw new ArgumentException("Use a nonnegative offset, limit of 1 to 25, and search of at most 200 characters."); }

		await using ApplicationDbContext db = await factory.CreateDbContextAsync(cancellationToken);
		await db.Database.OpenConnectionAsync(cancellationToken);
		await using var command = new NpgsqlCommand(Sql, (NpgsqlConnection)db.Database.GetDbConnection());
		command.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, request.Offset);
		command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, request.Limit);
		string? search = string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim();
		command.Parameters.AddWithValue("search", NpgsqlDbType.Text, (object?)search ?? DBNull.Value);
		command.Parameters.AddWithValue("conflicts", NpgsqlDbType.Boolean, request.ConflictsOnly);
		command.Parameters.AddWithValue("broad", NpgsqlDbType.Boolean, request.MigratedBroadOnly);
		command.Parameters.AddWithValue("unresolved", NpgsqlDbType.Boolean, request.UnresolvedOnly);
		command.Parameters.AddWithValue("unassigned", NpgsqlDbType.Boolean, request.UnassignedOnly);
		command.Parameters.AddWithValue("actor", NpgsqlDbType.Text, request.Actor);
		command.Parameters.AddWithValue("disposition_filter", NpgsqlDbType.Text, request.DispositionFilter.ToString());
		List<HistoricalGroupSummary> items = [];
		int total = 0;
		await using var reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			total = reader.GetInt32(0);
			if (reader.IsDBNull(1)) { continue; }
			var categories = JsonSerializer.Deserialize<List<CategoryRow>>(reader.GetString(9), JsonOptions) ?? [];
			items.Add(new HistoricalGroupSummary(
				new HistoricalGroupKey(reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)),
				reader.GetInt32(4), reader.GetBoolean(5), reader.GetBoolean(6),
				reader.GetBoolean(7), reader.GetBoolean(8),
								categories.Select(x => new HistoricalGroupCategory(x.CategoryId, x.Path, x.PurchaseCount)).ToArray(),
								reader.IsDBNull(10) ? null : Enum.Parse<HistoricalGroupDisposition>(reader.GetString(10)),
								reader.GetInt64(11)));
		}
		return new(items, total, (long)request.Offset + items.Count < total ? request.Offset + items.Count : null);
	}

	private sealed record CategoryRow(Guid? CategoryId, string? Path, int PurchaseCount);
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private const string Sql = """
        WITH RECURSIVE paths AS (
            SELECT n."CategoryId", n."ParentId", n."Name"::text AS path
            FROM taxonomy."Nodes" n JOIN taxonomy."Head" h ON h."RevisionId" = n."RevisionId"
            WHERE n."ParentId" IS NULL AND NOT n."Archived"
            UNION ALL
            SELECT n."CategoryId", n."ParentId", (p.path || ' / ' || n."Name")::text
            FROM taxonomy."Nodes" n JOIN taxonomy."Head" h ON h."RevisionId" = n."RevisionId"
            JOIN paths p ON p."CategoryId" = n."ParentId"
            WHERE NOT n."Archived"
        ), facts AS (
            SELECT r."Location" COLLATE "C" AS store,
                   i."Description" COLLATE "C" AS description,
                   i."ReceiptItemCode" COLLATE "C" AS code,
                   a."CategoryId" AS category_id,
                   a."Id" IS NULL AS unassigned,
                   a."Id" IS NOT NULL AND a."CategoryId" IS NULL AS unresolved,
                   a."Source" = 'legacy_import' AND a."CategoryId" IS NOT NULL
                       AND EXISTS (SELECT 1 FROM taxonomy."Nodes" child
                                   JOIN taxonomy."Head" h ON h."RevisionId" = child."RevisionId"
                                   WHERE child."ParentId" = a."CategoryId" AND NOT child."Archived") AS broad
            FROM receipts."ReceiptItems" i
            JOIN receipts."Receipts" r ON r."Id" = i."ReceiptId"
            LEFT JOIN taxonomy."CurrentAssignments" c ON c."ReceiptItemId" = i."Id"
            LEFT JOIN taxonomy."Assignments" a ON a."Id" = c."AssignmentId"
            WHERE i."DeletedAt" IS NULL AND r."DeletedAt" IS NULL
              AND (@search IS NULL OR strpos(lower(r."Location"), lower(@search)) > 0
                    OR strpos(lower(i."Description"), lower(@search)) > 0
                    OR strpos(lower(coalesce(i."ReceiptItemCode", '')), lower(@search)) > 0)
        ), groups AS MATERIALIZED (
            SELECT store, description, code, count(*)::integer AS purchase_count,
                   count(DISTINCT coalesce(category_id::text, '<unassigned>')) > 1 AS conflict,
                   bool_or(broad) AS broad, bool_or(unresolved) AS unresolved,
                   bool_or(unassigned) AS unassigned
            FROM facts GROUP BY store, description, code
        ), filtered AS MATERIALIZED (
            SELECT g.*, d."Disposition" AS disposition, coalesce(d."Version", 0) AS disposition_version
            FROM groups g LEFT JOIN taxonomy."HistoricalGroupDispositions" d
              ON d."Actor" = @actor AND d."Store" = g.store AND d."RawDescription" = g.description
              AND d."ItemCode" IS NOT DISTINCT FROM g.code
            WHERE (NOT @conflicts OR conflict)
              AND (NOT @broad OR broad) AND (NOT @unresolved OR unresolved)
              AND (NOT @unassigned OR unassigned)
              AND (@disposition_filter = 'All' OR
                   (@disposition_filter = 'Visible' AND d."Disposition" IS NULL) OR
                   d."Disposition" = @disposition_filter)
        ), page AS (
            SELECT * FROM filtered
            ORDER BY purchase_count DESC, store COLLATE "C", description COLLATE "C",
                     code COLLATE "C" NULLS FIRST
            OFFSET @offset LIMIT @limit
        )
        SELECT (SELECT count(*)::integer FROM filtered) AS total_groups,
               p.store, p.description, p.code, p.purchase_count, p.conflict,
               p.broad, p.unresolved, p.unassigned,
               coalesce((SELECT jsonb_agg(jsonb_build_object(
                   'categoryId', counts.category_id, 'path', paths.path,
                   'purchaseCount', counts.purchase_count)
                   ORDER BY counts.purchase_count DESC, counts.category_id NULLS FIRST)::text
                   FROM (SELECT f.category_id, count(*)::integer AS purchase_count
                         FROM facts f WHERE f.store = p.store AND f.description = p.description
                           AND f.code IS NOT DISTINCT FROM p.code
                         GROUP BY f.category_id) counts
                   LEFT JOIN paths ON paths."CategoryId" = counts.category_id), '[]') AS categories,
               p.disposition, p.disposition_version
        FROM (SELECT 1) anchor LEFT JOIN page p ON true
        """;
}
