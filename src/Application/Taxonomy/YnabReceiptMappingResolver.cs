using Application.Envelopes;
using Application.Models.Ynab;
using Domain.Core;
using Domain.Envelopes;
using Domain.Taxonomy;

namespace Application.Taxonomy;

/// <summary>
/// Resolves every item's YNAB category from its recorded envelope before split arithmetic begins.
/// A missing envelope or an envelope with no YNAB category in the budget is unmapped.
/// </summary>
public static class YnabReceiptMappingResolver
{
	public static (Dictionary<string, string> ItemTargets, List<string> Unmapped) Resolve(
		IReadOnlyList<ReceiptItem> items, string budgetId, IReadOnlyList<YnabEnvelopeMappingDto> mappings,
		IReadOnlyDictionary<Guid, string> envelopeNames)
		=> ResolveEnvelopes(items.Select(item => (item, item.EnvelopeId)), budgetId, mappings, envelopeNames);

	/// <summary>
	/// Preview under the current item-type defaults: envelopes that follow the item type are re-derived
	/// from the current tree; receipt, item and historical envelopes are kept, as a save would keep them.
	/// </summary>
	public static (Dictionary<string, string> ItemTargets, List<string> Unmapped) ResolveCurrent(
		IReadOnlyList<ReceiptItem> items, string budgetId, IReadOnlyList<YnabEnvelopeMappingDto> mappings,
		IReadOnlyDictionary<Guid, string> envelopeNames, TaxonomyRevision revision, IReadOnlyDictionary<Guid, Guid> defaults)
	{
		Dictionary<Guid, TaxonomyNode> nodes = revision.Nodes.GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First());
		return ResolveEnvelopes(items.Select(item =>
		{
			EnvelopeAssignment stored = new(item.EnvelopeId, item.EnvelopeSource);
			return (item, EnvelopeDerivationPolicy.FollowsItemType(stored)
				? EnvelopeDerivationPolicy.AfterItemTypeChange(stored,
					EnvelopeDerivationPolicy.ResolveDefault(item.CurrentClassification?.CategoryId, nodes, defaults)).EnvelopeId
				: stored.EnvelopeId);
		}), budgetId, mappings, envelopeNames);
	}

	public static string ItemKey(Guid itemId) => $"item:{itemId:D}";

	private static (Dictionary<string, string> ItemTargets, List<string> Unmapped) ResolveEnvelopes(
		IEnumerable<(ReceiptItem Item, Guid? EnvelopeId)> items, string budgetId,
		IReadOnlyList<YnabEnvelopeMappingDto> mappings, IReadOnlyDictionary<Guid, string> envelopeNames)
	{
		Dictionary<Guid, string> targetsByEnvelope = mappings.Where(m => m.YnabBudgetId == budgetId)
			.ToDictionary(m => m.EnvelopeId, m => m.YnabCategoryId);
		Dictionary<string, string> targets = new(StringComparer.Ordinal);
		List<string> unmapped = [];
		foreach ((ReceiptItem item, Guid? envelopeId) in items)
		{
			if (envelopeId is not Guid envelope)
			{
				unmapped.Add($"No envelope (item {item.Id:D}: Unresolved)");
				continue;
			}
			if (!targetsByEnvelope.TryGetValue(envelope, out string? target))
			{
				unmapped.Add($"{envelopeNames.GetValueOrDefault(envelope) ?? envelope.ToString("D")} (item {item.Id:D}: Unmapped)");
				continue;
			}
			targets.Add(ItemKey(item.Id), target);
		}
		return (targets, unmapped);
	}
}
