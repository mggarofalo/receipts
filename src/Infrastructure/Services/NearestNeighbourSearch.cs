using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Services;

/// <summary>
/// The rule every kNN query over the HNSW index follows (RECEIPTS-1097, docs/semantic-vector-rebuild.md).
/// An HNSW scan probes <c>hnsw.ef_search</c> (40) neighbours and only then applies the WHERE clause, so
/// while most rows sit on a stale fingerprint a <c>LIMIT 20</c> can return few rows or none. A strict
/// iterative scan keeps probing until the limit is met, in exact distance order.
/// </summary>
internal static class NearestNeighbourSearch
{
	/// <summary>Needs pgvector 0.8 or later; the image is pinned to 0.8.7.</summary>
	private const string IterativeScan = "SET LOCAL hnsw.iterative_scan = strict_order";

	/// <summary>
	/// Runs <paramref name="query"/> with the iterative scan. SET LOCAL lasts until the transaction ends,
	/// so inside a caller's transaction it stays on for the rest of that transaction.
	/// </summary>
	public static async Task<List<T>> ToListAsync<T>(DatabaseFacade database, IQueryable<T> query, CancellationToken cancellationToken)
	{
		if (database.CurrentTransaction is not null)
		{
			await database.ExecuteSqlRawAsync(IterativeScan, cancellationToken);
			return await query.ToListAsync(cancellationToken);
		}
		await using IDbContextTransaction transaction = await database.BeginTransactionAsync(cancellationToken);
		await database.ExecuteSqlRawAsync(IterativeScan, cancellationToken);
		List<T> rows = await query.ToListAsync(cancellationToken);
		await transaction.CommitAsync(cancellationToken);
		return rows;
	}
}
