namespace Application.Envelopes;

/// <summary>The YNAB category each item reaches through its envelope, per budget; null when unmapped.</summary>
public sealed record EnvelopeYnabRoute(Guid ItemId, string BudgetId, string? YnabCategoryId);

public sealed record EnvelopeBaselineReport(int Items, int Unchanged, int Changed, int Added, int Removed)
{
	public bool Holds => Changed == 0 && Removed == 0;
}

/// <summary>
/// Restructure-safe budget parity: compares the YNAB category every item reaches through its envelope
/// with a baseline captured earlier (for example before applying a restructure plan). Unlike
/// <see cref="EnvelopeParity"/>, it does not consult the retired node mappings, which a restructure
/// intentionally decouples from item types. Reports counts only.
/// </summary>
public static class EnvelopeBaseline
{
	public static IReadOnlyList<EnvelopeYnabRoute> Routes(IEnumerable<(Guid ItemId, Guid? EnvelopeId)> items,
		IReadOnlyDictionary<(Guid EnvelopeId, string BudgetId), string> envelopeLinks)
	{
		string[] budgets = envelopeLinks.Keys.Select(key => key.BudgetId).Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal).ToArray();
		return [.. items.OrderBy(item => item.ItemId).SelectMany(item => budgets.Select(budget => new EnvelopeYnabRoute(
			item.ItemId, budget, item.EnvelopeId is Guid envelope ? envelopeLinks.GetValueOrDefault((envelope, budget)) : null)))];
	}

	/// <summary>An item counts as changed when any budget's route differs; budgets absent on one side count as unmapped.</summary>
	public static EnvelopeBaselineReport Compare(IReadOnlyList<EnvelopeYnabRoute> baseline, IReadOnlyList<EnvelopeYnabRoute> current)
	{
		ILookup<Guid, EnvelopeYnabRoute> before = baseline.ToLookup(route => route.ItemId);
		ILookup<Guid, EnvelopeYnabRoute> after = current.ToLookup(route => route.ItemId);
		HashSet<Guid> beforeIds = [.. baseline.Select(route => route.ItemId)];
		HashSet<Guid> afterIds = [.. current.Select(route => route.ItemId)];
		int unchanged = 0, changed = 0;
		foreach (Guid item in afterIds.Where(beforeIds.Contains))
		{
			Dictionary<string, string?> old = before[item].ToDictionary(route => route.BudgetId, route => route.YnabCategoryId, StringComparer.Ordinal);
			Dictionary<string, string?> now = after[item].ToDictionary(route => route.BudgetId, route => route.YnabCategoryId, StringComparer.Ordinal);
			bool differs = old.Keys.Union(now.Keys, StringComparer.Ordinal).Any(budget =>
				!string.Equals(old.GetValueOrDefault(budget), now.GetValueOrDefault(budget), StringComparison.Ordinal));
			if (differs) { changed++; } else { unchanged++; }
		}
		return new(afterIds.Count, unchanged, changed, afterIds.Count(id => !beforeIds.Contains(id)), beforeIds.Count(id => !afterIds.Contains(id)));
	}
}
