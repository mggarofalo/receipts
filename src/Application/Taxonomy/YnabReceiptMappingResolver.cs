using Application.Models.Ynab;
using Domain.Core;

namespace Application.Taxonomy;

/// <summary>Resolves every item from its pinned classification before split arithmetic begins.</summary>
public static class YnabReceiptMappingResolver
{
	public static string ItemKey(Guid itemId) => $"item:{itemId:D}";

	public static (Dictionary<string, string> ItemTargets, List<string> Unmapped) Resolve(
		IReadOnlyList<ReceiptItem> items, string budgetId, IReadOnlyList<YnabCategoryMappingDto> mappings)
	{
		ExternalCategoryScope scope = new("YNAB", budgetId);
		ExternalCategoryMapping[] stableMappings = mappings
			.Where(m => m.ReceiptsCategoryId.HasValue && m.YnabBudgetId == budgetId)
			.Select(m => new ExternalCategoryMapping(m.Id, m.ReceiptsCategoryId!.Value, scope, m.YnabCategoryId))
			.ToArray();
		Dictionary<string, string> targets = new(StringComparer.Ordinal);
		List<string> unmapped = [];
		foreach (ReceiptItem item in items)
		{
			ExternalCategoryMappingResolution result = ExternalCategoryMappingPolicy.ResolveRecorded(
				item.CurrentClassification, scope, stableMappings);
			if (result.Disposition != ExternalCategoryMappingDisposition.Mapped || result.Mapping is null)
			{
				unmapped.Add($"{item.Category} (item {item.Id:D}: {result.Disposition})");
				continue;
			}
			targets.Add(ItemKey(item.Id), result.Mapping.ExternalCategoryId);
		}
		return (targets, unmapped);
	}
}
