using Application.Taxonomy;
using Domain.Taxonomy;

namespace Application.Envelopes;

public sealed record EnvelopeParityItem(Guid ItemId, CurrentClassification? Classification, Guid? EnvelopeId);

public sealed record EnvelopeParityReport(int Items, int Envelopes, int Budgets, int ItemsMappedByOldPath,
	int ItemsMappedByNewPath, int Mismatches)
{
	public bool Holds => Mismatches == 0;
}

/// <summary>
/// The backfill invariant: for every item and every budget, the YNAB category reached through the
/// item's envelope equals the one its recorded classification resolves to via the retired
/// node-keyed mappings. Reports counts only.
/// </summary>
public static class EnvelopeParity
{
	public static EnvelopeParityReport Check(IReadOnlyList<EnvelopeParityItem> items,
		IReadOnlyList<LegacyYnabCategoryMapping> legacyMappings, int envelopeCount,
		IReadOnlyDictionary<(Guid EnvelopeId, string BudgetId), string> envelopeLinks)
	{
		Dictionary<string, ExternalCategoryMapping[]> byBudget = EnvelopeBackfillPlanner.BoundMappingsByBudget(legacyMappings);
		string[] budgets = byBudget.Keys.Union(envelopeLinks.Keys.Select(key => key.BudgetId), StringComparer.Ordinal)
			.Order(StringComparer.Ordinal).ToArray();
		int oldMapped = 0, newMapped = 0, mismatches = 0;
		foreach (EnvelopeParityItem item in items)
		{
			bool anyOld = false, anyNew = false, differs = false;
			foreach (string budget in budgets)
			{
				string? expected = null;
				if (byBudget.TryGetValue(budget, out ExternalCategoryMapping[]? mappings))
				{
					ExternalCategoryMappingResolution result = ExternalCategoryMappingPolicy.ResolveRecorded(
						item.Classification, new(EnvelopeBackfillPlanner.YnabProvider, budget), mappings);
					expected = result.Disposition == ExternalCategoryMappingDisposition.Mapped ? result.Mapping?.ExternalCategoryId : null;
				}
				string? actual = item.EnvelopeId is Guid envelope ? envelopeLinks.GetValueOrDefault((envelope, budget)) : null;
				anyOld |= expected is not null;
				anyNew |= actual is not null;
				differs |= !string.Equals(expected, actual, StringComparison.Ordinal);
			}
			oldMapped += anyOld ? 1 : 0;
			newMapped += anyNew ? 1 : 0;
			mismatches += differs ? 1 : 0;
		}
		return new(items.Count, envelopeCount, budgets.Length, oldMapped, newMapped, mismatches);
	}
}
