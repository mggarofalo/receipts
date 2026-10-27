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
		return candidates.Where(candidate => ReceiptSuggestionEvidence.IsDescriptionCompletion(request.SearchText, candidate.Name))
				.Take(request.Limit);
	}
}
