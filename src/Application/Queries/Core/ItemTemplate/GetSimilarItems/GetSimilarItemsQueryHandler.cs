using Application.Interfaces.Services;
using Application.Normalization;
using Mediator;

namespace Application.Queries.Core.ItemTemplate.GetSimilarItems;

public class GetSimilarItemsQueryHandler(IItemTemplateSimilarityService similarityService) : IRequestHandler<GetSimilarItemsQuery, IEnumerable<SimilarItemResult>>
{
	public async ValueTask<IEnumerable<SimilarItemResult>> Handle(GetSimilarItemsQuery request, CancellationToken cancellationToken)
	{
		// Fetch a bounded reserve so packaging-only matches cannot
		// crowd usable completions out of the requested page.
		var candidates = await similarityService.GetSimilarItemsAsync(request.SearchText,
				Math.Min(request.Limit * 5, 100), request.Threshold, request.UseSemanticSearch, cancellationToken);
		return candidates.Where(candidate => IsUseful(request.SearchText, candidate)).Take(request.Limit);
	}

	/// <summary>
	/// A typed completion, or a semantic neighbour close enough to stand on similarity alone (RECEIPTS-1098):
	/// "chicken br" completes nothing in "KS CHKN BRST", so only its vector can offer it.
	/// </summary>
	private static bool IsUseful(string searchText, SimilarItemResult candidate) =>
		candidate.AdmittedBySemanticSearchOnly
			? candidate.SemanticSimilarity >= ReceiptSuggestionEvidence.SemanticFloor
			: ReceiptSuggestionEvidence.IsDescriptionCompletion(searchText, candidate.Name);
}
