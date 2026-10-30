using Application.Models.Ynab;
using Domain.Envelopes;
using Domain.Taxonomy;

namespace Application.Tests.Commands.Ynab;

internal static class YnabRecordedCategoryFixture
{
	internal static readonly Guid GroceriesId = Guid.Parse("78c5a71d-9ee1-4c60-a2d1-baf9c6f68421");
	internal static readonly Guid GroceriesEnvelopeId = Guid.Parse("0f4c7f59-2d3b-4a5c-9a43-5f1e9d2b7c11");
	private static readonly Guid RevisionId = Guid.Parse("3efad32b-85e7-4a85-97a0-35891ee53b1d");

	internal static Envelope GroceriesEnvelope => new(GroceriesEnvelopeId, "Groceries");

	/// <summary>Classifies the item as Groceries and records the Groceries envelope on it, as a save would.</summary>
	internal static Domain.Core.ReceiptItem Classified(Domain.Core.ReceiptItem item)
	{
		item.CurrentClassification = new CurrentClassification(
			Guid.NewGuid(), GroceriesId, RevisionId,
			[new TaxonomyNode(GroceriesId, null, "Groceries", null, false)]);
		item.EnvelopeId = GroceriesEnvelopeId;
		item.EnvelopeSource = EnvelopeSource.Default;
		return item;
	}

	/// <summary>Maps the Groceries envelope to a YNAB category in a budget.</summary>
	internal static YnabEnvelopeMappingDto Mapped(string ynabCategoryId, string ynabCategoryName,
		string ynabCategoryGroupName, string budgetId) =>
		new(Guid.NewGuid(), GroceriesEnvelopeId, "Groceries", ynabCategoryId, ynabCategoryName,
			ynabCategoryGroupName, budgetId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
