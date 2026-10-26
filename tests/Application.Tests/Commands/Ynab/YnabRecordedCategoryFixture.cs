using Application.Models.Ynab;
using Domain.Taxonomy;

namespace Application.Tests.Commands.Ynab;

internal static class YnabRecordedCategoryFixture
{
	internal static readonly Guid GroceriesId = Guid.Parse("78c5a71d-9ee1-4c60-a2d1-baf9c6f68421");
	private static readonly Guid RevisionId = Guid.Parse("3efad32b-85e7-4a85-97a0-35891ee53b1d");

	internal static Domain.Core.ReceiptItem Classified(Domain.Core.ReceiptItem item)
	{
		item.CurrentClassification = new CurrentClassification(
			Guid.NewGuid(), GroceriesId, RevisionId,
			[new TaxonomyNode(GroceriesId, null, "Groceries", null, false)]);
		return item;
	}

	internal static YnabCategoryMappingDto Mapped(YnabCategoryMappingDto mapping) =>
		mapping with { ReceiptsCategoryId = GroceriesId };
}
