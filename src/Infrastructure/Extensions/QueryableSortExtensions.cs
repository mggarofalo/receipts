using System.Linq.Expressions;
using Application.Models;

namespace Infrastructure.Extensions;

public static class QueryableSortExtensions
{
	/// <summary>
	/// Applies the requested (or default) primary sort, then a mandatory ascending
	/// tiebreaker on a unique key so the total order is deterministic. Without the
	/// tiebreaker, rows sharing the primary sort value (e.g. many receipts on the same
	/// Date) have no stable order across separate queries, which lets consecutive
	/// paginated requests duplicate one row and skip another (RECEIPTS-767).
	/// </summary>
	/// <param name="tiebreaker">
	/// A unique-key selector (typically <c>e =&gt; e.Id</c>) applied ascending regardless
	/// of the primary sort direction. Required so no paged query is left non-deterministic.
	/// </param>
	public static IOrderedQueryable<T> ApplySort<T>(
		this IQueryable<T> query,
		SortParams sort,
		Dictionary<string, Expression<Func<T, object>>> allowedColumns,
		Expression<Func<T, object>> defaultSort,
		Expression<Func<T, object>> tiebreaker,
		bool defaultDescending = false)
	{
		Expression<Func<T, object>> sortExpression = defaultSort;
		bool descending = defaultDescending;

		if (!string.IsNullOrWhiteSpace(sort.SortBy)
			&& allowedColumns.TryGetValue(sort.SortBy, out Expression<Func<T, object>>? column))
		{
			sortExpression = column;
			descending = sort.IsDescending;
		}

		IOrderedQueryable<T> ordered = descending
			? query.OrderByDescending(sortExpression)
			: query.OrderBy(sortExpression);

		return ordered.ThenBy(tiebreaker);
	}
}
