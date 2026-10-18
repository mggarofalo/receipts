using System.Text.RegularExpressions;
using Application.Models.Reports;

namespace Application.Duplicates;

/// <summary>Stable seed-relative groups: proximity is intentionally not transitive.</summary>
public static partial class DuplicateDetectionPolicy
{
	[GeneratedRegex(@"\s+")]
	private static partial Regex WhitespaceRegex();

	public static List<DuplicateGroup> Group(IReadOnlyList<DuplicateReceiptSummary> receipts, string matchOn, string locationTolerance, decimal totalTolerance)
	{
		bool normalized = locationTolerance.Equals("normalized", StringComparison.OrdinalIgnoreCase);

		string NormalizeLocation(string location)
		{
			string trimmed = location.Trim().ToLowerInvariant();
			return WhitespaceRegex().Replace(trimmed, " ");
		}

		bool TotalsMatch(decimal a, decimal b) => Math.Abs(a - b) <= totalTolerance;

		return matchOn.ToLowerInvariant() switch
		{
			"dateandlocation" => receipts.OrderBy(r => r.Date).ThenBy(r => r.Location, StringComparer.Ordinal).ThenBy(r => r.ReceiptId)
				.GroupBy(r => (r.Date, Location: normalized ? NormalizeLocation(r.Location) : r.Location))
				.Where(g => g.Count() > 1)
				.Select(g => new DuplicateGroup(
					$"{g.Key.Date:yyyy-MM-dd} @ {g.First().Location}",
					g.ToList()))
				.ToList(),

			"dateandtotal" => ClusterByTotal(
				receipts.OrderBy(r => r.Date).ThenBy(r => r.TransactionTotal).ThenBy(r => r.ReceiptId).GroupBy(r => r.Date),
				TotalsMatch,
				dateGroup => dateGroup.Key,
				(date, seed) => $"{date:yyyy-MM-dd} — ${seed.TransactionTotal:F2}"),

			_ => ClusterByTotal(
				receipts.OrderBy(r => r.Date).ThenBy(r => r.TransactionTotal).ThenBy(r => r.ReceiptId).GroupBy(r => (r.Date, Location: normalized ? NormalizeLocation(r.Location) : r.Location)),
				TotalsMatch,
				locDateGroup => locDateGroup.Key,
				(key, seed) => $"{key.Date:yyyy-MM-dd} @ {seed.Location} — ${seed.TransactionTotal:F2}")
		};

	}
	private static List<DuplicateGroup> ClusterByTotal<TKey>(
		IEnumerable<IGrouping<TKey, DuplicateReceiptSummary>> groupedReceipts,
		Func<decimal, decimal, bool> totalsMatch,
		Func<IGrouping<TKey, DuplicateReceiptSummary>, TKey> keySelector,
		Func<TKey, DuplicateReceiptSummary, string> formatMatchKey)
	{
		List<DuplicateGroup> result = [];

		foreach (IGrouping<TKey, DuplicateReceiptSummary> group in groupedReceipts)
		{
			List<DuplicateReceiptSummary> ordered = [.. group];
			TKey key = keySelector(group);
			int index = 0;
			while (index < ordered.Count)
			{
				int start = index++;
				DuplicateReceiptSummary seed = ordered[start];
				while (index < ordered.Count && totalsMatch(seed.TransactionTotal, ordered[index].TransactionTotal))
				{
					index++;
				}

				if (index - start > 1)
				{
					result.Add(new DuplicateGroup(formatMatchKey(key, seed), ordered.GetRange(start, index - start)));
				}
			}

		}

		return result;
	}

}
