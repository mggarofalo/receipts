using Application.Queries.Core.ItemTemplate.GetSimilarItems;

namespace Application.Interfaces.Services;

public interface IItemTemplateSimilarityService
{
	Task<List<SimilarItemResult>> GetSimilarItemsAsync(string searchText, int limit, double threshold, bool useSemanticSearch, CancellationToken cancellationToken);
}
