using Application.Models.Reports;

namespace Application.Duplicates;

public static class DuplicateAcceptancePolicy
{
	public static List<(Guid A, Guid B)> CanonicalPairs(IEnumerable<Guid> receiptIds)
	{
		Guid[] ids = [.. receiptIds.Distinct().Order()];
		List<(Guid A, Guid B)> pairs = [];
		for (int i = 0; i < ids.Length; i++)
		{
			for (int j = i + 1; j < ids.Length; j++)
			{
				pairs.Add((ids[i], ids[j]));
			}
		}

		return pairs;
	}

	private static long RequiredPairCount(int count) => (long)count * (count - 1) / 2;

	public static bool IsFullyAccepted(int receiptCount, long acceptedPairCount)
		=> receiptCount >= 2 && acceptedPairCount == RequiredPairCount(receiptCount);

	public static bool IsFullyAccepted(IEnumerable<Guid> receiptIds, HashSet<(Guid A, Guid B)> acceptedPairs)
	{
		Guid[] ids = [.. receiptIds.Distinct().Order()];
		if (ids.Length < 2 || RequiredPairCount(ids.Length) > acceptedPairs.Count)
		{
			return false;
		}

		for (int i = 0; i < ids.Length; i++)
		{
			for (int j = i + 1; j < ids.Length; j++)
			{
				if (!acceptedPairs.Contains((ids[i], ids[j])))
				{
					return false;
				}
			}
		}

		return true;
	}

	public static AcceptedDuplicatesResult ProjectGroups(IReadOnlyList<DuplicateAcceptanceComponent> components, IReadOnlyDictionary<Guid, DuplicateReceiptSummary> activeReceipts)
	{
		List<AcceptedDuplicateGroup> groups = [];
		foreach (DuplicateAcceptanceComponent component in components)
		{
			List<DuplicateReceiptSummary> visible = [.. component.ReceiptIds.Where(activeReceipts.ContainsKey)
				.Select(id => activeReceipts[id]).OrderBy(r => r.Date).ThenBy(r => r.Location, StringComparer.Ordinal).ThenBy(r => r.ReceiptId)];
			if (visible.Count >= 2)
			{
				groups.Add(new(visible, component.ReceiptIds, component.AcceptedAt));
			}
		}
		groups = [.. groups.OrderByDescending(g => g.AcceptedAt).ThenBy(g => g.MemberReceiptIds[0])];
		return new(groups, groups.Count);
	}

	/// <summary>Connected components describe undo membership, not all-pair acceptance.</summary>
	public static List<DuplicateAcceptanceComponent> Components(IReadOnlyList<DuplicateAcceptedPair> pairs)
	{
		Dictionary<Guid, HashSet<Guid>> neighbors = [];
		Dictionary<Guid, DateTimeOffset> latest = [];
		foreach (DuplicateAcceptedPair pair in pairs)
		{
			foreach ((Guid from, Guid to) in new[] { (pair.ReceiptIdA, pair.ReceiptIdB), (pair.ReceiptIdB, pair.ReceiptIdA) })
			{
				if (!neighbors.TryGetValue(from, out HashSet<Guid>? connected))
				{
					neighbors[from] = connected = [];
				}

				connected.Add(to);
				if (!latest.TryGetValue(from, out DateTimeOffset at) || pair.AcceptedAt > at)
				{
					latest[from] = pair.AcceptedAt;
				}
			}
		}
		HashSet<Guid> visited = [];
		List<DuplicateAcceptanceComponent> components = [];
		foreach (Guid root in neighbors.Keys.Order())
		{
			if (!visited.Add(root))
			{
				continue;
			}

			Queue<Guid> pending = new();
			pending.Enqueue(root);
			List<Guid> members = [];
			DateTimeOffset acceptedAt = latest[root];
			while (pending.TryDequeue(out Guid id))
			{
				members.Add(id);
				if (latest[id] > acceptedAt)
				{
					acceptedAt = latest[id];
				}

				foreach (Guid neighbor in neighbors[id])
				{
					if (visited.Add(neighbor))
					{
						pending.Enqueue(neighbor);
					}
				}
			}
			members.Sort();
			components.Add(new(members, acceptedAt));
		}
		return components;
	}
}
