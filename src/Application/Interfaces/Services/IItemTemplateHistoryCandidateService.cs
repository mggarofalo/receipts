using Application.Models;
using Application.Queries.Core.ItemTemplate.GetHistoryCandidates;

namespace Application.Interfaces.Services;

/// <summary>
/// Surfaces recurring receipt-item descriptions that have no matching item template yet.
/// Kept separate from <see cref="IItemTemplateSimilarityService"/>: that service answers
/// "what looks like this text?" per keystroke, while this one aggregates the whole history
/// into a paged worklist.
/// </summary>
public interface IItemTemplateHistoryCandidateService
{
	Task<PagedResult<ItemTemplateHistoryCandidate>> GetHistoryCandidatesAsync(int offset, int limit, int minCount, CancellationToken cancellationToken);
}
