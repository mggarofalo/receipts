using Application.Taxonomy;
using Domain.Taxonomy;

namespace Application.Envelopes;

/// <summary>A frozen node-keyed YNAB mapping, the evidence the envelope backfill reads.</summary>
public sealed record LegacyYnabCategoryMapping(Guid Id, Guid? CategoryId, string BudgetId,
	string YnabCategoryId, string YnabCategoryName, string YnabCategoryGroupName, DateTimeOffset UpdatedAt);

public sealed record EnvelopeBackfillItem(Guid ItemId, CurrentClassification? Classification);

public sealed record YnabEnvelopeLink(string BudgetId, string YnabCategoryId, string YnabCategoryName,
	string YnabCategoryGroupName);

public sealed record PlannedEnvelope(Guid Id, string Name, bool IsActive, IReadOnlyList<YnabEnvelopeLink> Links);

public sealed record EnvelopeBackfillPlan(IReadOnlyList<PlannedEnvelope> Envelopes,
	IReadOnlyDictionary<Guid, Guid?> ItemEnvelopes, IReadOnlyDictionary<Guid, Guid> Defaults);

/// <summary>
/// Derives envelopes from today's node-keyed YNAB mappings with exact budget parity: an item's
/// envelope pushes to the same YNAB category, in every budget, as its recorded classification
/// resolves to through <see cref="ExternalCategoryMappingPolicy.ResolveRecorded"/>.
/// Every name comes from the data (YNAB category or taxonomy root names).
/// </summary>
public static class EnvelopeBackfillPlanner
{
	public const string YnabProvider = "YNAB";

	public static EnvelopeBackfillPlan Plan(IReadOnlyList<EnvelopeBackfillItem> items,
		IReadOnlyList<LegacyYnabCategoryMapping> mappings, Guid currentRevisionId,
		IReadOnlyList<TaxonomyNode> currentNodes, string? selectedBudgetId, Func<Guid> newId)
	{
		Dictionary<(string Budget, string Category), YnabEnvelopeLink> links = LatestLinks(mappings);
		Dictionary<string, ExternalCategoryMapping[]> byBudget = BoundMappingsByBudget(mappings);

		// Signature: every budget's resolved YNAB category, so parity holds for each budget.
		Dictionary<string, List<YnabEnvelopeLink>> signatures = new(StringComparer.Ordinal);
		Dictionary<Guid, string> itemSignatures = [];
		Dictionary<Guid, Guid> itemRoots = [];
		Dictionary<Guid, string> recordedRootNames = [];
		foreach (EnvelopeBackfillItem item in items)
		{
			List<YnabEnvelopeLink> resolved = [];
			foreach ((string budget, ExternalCategoryMapping[] budgetMappings) in byBudget)
			{
				ExternalCategoryMappingResolution result = ExternalCategoryMappingPolicy.ResolveRecorded(
					item.Classification, new(YnabProvider, budget), budgetMappings);
				if (result.Disposition == ExternalCategoryMappingDisposition.Mapped && result.Mapping is not null)
				{
					resolved.Add(links[(budget, result.Mapping.ExternalCategoryId)]);
				}
			}
			if (resolved.Count > 0)
			{
				string key = Key(resolved);
				signatures.TryAdd(key, resolved);
				itemSignatures[item.ItemId] = key;
			}
			else if (item.Classification is { CategoryId: not null, RecordedPath.Count: > 0 } classification)
			{
				TaxonomyNode root = classification.RecordedPath[0];
				itemRoots[item.ItemId] = root.Id;
				recordedRootNames.TryAdd(root.Id, root.Name);
			}
		}

		// Defaults: each mapped item type keeps today's resolution for future purchases.
		Dictionary<Guid, string> defaultSignatures = [];
		HashSet<Guid> mappedNodes = byBudget.Values.SelectMany(list => list).Select(m => m.CategoryId).ToHashSet();
		foreach (TaxonomyNode node in currentNodes.Where(node => mappedNodes.Contains(node.Id) && !node.Archived))
		{
			List<YnabEnvelopeLink> resolved = [];
			foreach ((string budget, ExternalCategoryMapping[] budgetMappings) in byBudget)
			{
				ExternalCategoryMappingResolution result = ExternalCategoryMappingPolicy.ResolveCurrent(
					node.Id, currentRevisionId, currentNodes, new(YnabProvider, budget), budgetMappings);
				if (result.Disposition == ExternalCategoryMappingDisposition.Mapped && result.Mapping is not null)
				{
					resolved.Add(links[(budget, result.Mapping.ExternalCategoryId)]);
				}
			}
			if (resolved.Count > 0)
			{
				string key = Key(resolved);
				signatures.TryAdd(key, resolved);
				defaultSignatures[node.Id] = key;
			}
		}

		// Unbound legacy rows still name a YNAB category the owner chose; keep each as a linked envelope.
		HashSet<(string, string)> linked = signatures.Values.SelectMany(list => list)
			.Select(link => (link.BudgetId, link.YnabCategoryId)).ToHashSet();
		foreach (LegacyYnabCategoryMapping unbound in mappings.Where(m => m.CategoryId is null)
			.OrderBy(m => m.BudgetId, StringComparer.Ordinal).ThenBy(m => m.YnabCategoryId, StringComparer.Ordinal))
		{
			if (linked.Add((unbound.BudgetId, unbound.YnabCategoryId)))
			{
				List<YnabEnvelopeLink> single = [links[(unbound.BudgetId, unbound.YnabCategoryId)]];
				signatures.TryAdd(Key(single), single);
			}
		}

		Dictionary<Guid, string> currentNames = currentNodes.GroupBy(node => node.Id)
			.ToDictionary(group => group.Key, group => group.First().Name);
		HashSet<string> usedNames = new(StringComparer.Ordinal);
		List<PlannedEnvelope> envelopes = [];
		Dictionary<string, Guid> envelopeBySignature = new(StringComparer.Ordinal);
		foreach ((string key, List<YnabEnvelopeLink> signature) in signatures
			.Select(pair => (pair.Key, pair.Value, Name: PreferredName(pair.Value, selectedBudgetId)))
			.OrderBy(entry => entry.Name, StringComparer.Ordinal).ThenBy(entry => entry.Key, StringComparer.Ordinal)
			.Select(entry => (entry.Key, entry.Value)))
		{
			Guid id = newId();
			envelopeBySignature[key] = id;
			envelopes.Add(new(id, UniqueName(PreferredName(signature, selectedBudgetId), usedNames), true, signature));
		}
		Dictionary<Guid, Guid> envelopeByRoot = [];
		foreach ((Guid rootId, string name) in itemRoots.Values.Distinct()
			.Select(rootId => (rootId, currentNames.GetValueOrDefault(rootId) ?? recordedRootNames[rootId]))
			.OrderBy(entry => entry.Item2, StringComparer.Ordinal).ThenBy(entry => entry.rootId))
		{
			Guid id = newId();
			envelopeByRoot[rootId] = id;
			envelopes.Add(new(id, UniqueName(name, usedNames), false, []));
		}

		Dictionary<Guid, Guid?> itemEnvelopes = items.ToDictionary(item => item.ItemId,
			item => itemSignatures.TryGetValue(item.ItemId, out string? key) ? envelopeBySignature[key]
				: itemRoots.TryGetValue(item.ItemId, out Guid root) ? envelopeByRoot[root] : (Guid?)null);
		Dictionary<Guid, Guid> defaults = defaultSignatures.ToDictionary(pair => pair.Key, pair => envelopeBySignature[pair.Value]);
		return new(envelopes, itemEnvelopes, defaults);
	}

	internal static Dictionary<string, ExternalCategoryMapping[]> BoundMappingsByBudget(
		IReadOnlyList<LegacyYnabCategoryMapping> mappings)
		=> mappings.Where(m => m.CategoryId.HasValue)
			.GroupBy(m => m.BudgetId, StringComparer.Ordinal)
			.OrderBy(group => group.Key, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.Select(m => new ExternalCategoryMapping(m.Id,
				m.CategoryId!.Value, new(YnabProvider, group.Key), m.YnabCategoryId)).ToArray(), StringComparer.Ordinal);

	private static Dictionary<(string, string), YnabEnvelopeLink> LatestLinks(IReadOnlyList<LegacyYnabCategoryMapping> mappings)
		=> mappings.GroupBy(m => (m.BudgetId, m.YnabCategoryId))
			.ToDictionary(group => group.Key, group =>
			{
				LegacyYnabCategoryMapping latest = group.OrderByDescending(m => m.UpdatedAt).ThenBy(m => m.Id).First();
				return new YnabEnvelopeLink(latest.BudgetId, latest.YnabCategoryId, latest.YnabCategoryName, latest.YnabCategoryGroupName);
			});

	private static string Key(IEnumerable<YnabEnvelopeLink> links)
		=> string.Join('\u001e', links.OrderBy(link => link.BudgetId, StringComparer.Ordinal)
			.Select(link => link.BudgetId + '\u001f' + link.YnabCategoryId));

	private static string PreferredName(IReadOnlyList<YnabEnvelopeLink> links, string? selectedBudgetId)
	{
		YnabEnvelopeLink link = links.FirstOrDefault(link => link.BudgetId == selectedBudgetId)
			?? links.OrderBy(link => link.BudgetId, StringComparer.Ordinal).First();
		return string.IsNullOrWhiteSpace(link.YnabCategoryName) ? link.YnabCategoryId : link.YnabCategoryName;
	}

	public const int MaxNameLength = 200;

	private static string Fit(string name, string suffix)
		=> (name.Length + suffix.Length > MaxNameLength ? name[..(MaxNameLength - suffix.Length)] : name) + suffix;

	private static string UniqueName(string name, HashSet<string> used)
	{
		string candidate = Fit(name, "");
		for (int suffix = 2; !used.Add(candidate); suffix++)
		{
			candidate = Fit(name, $" ({suffix})");
		}
		return candidate;
	}
}
