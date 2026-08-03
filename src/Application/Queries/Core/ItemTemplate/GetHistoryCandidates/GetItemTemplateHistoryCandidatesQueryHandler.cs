using Application.Interfaces.Services;
using Application.Models;
using Mediator;

namespace Application.Queries.Core.ItemTemplate.GetHistoryCandidates;

public class GetItemTemplateHistoryCandidatesQueryHandler(IItemTemplateHistoryCandidateService candidateService)
	: IRequestHandler<GetItemTemplateHistoryCandidatesQuery, PagedResult<ItemTemplateHistoryCandidate>>
{
	public async ValueTask<PagedResult<ItemTemplateHistoryCandidate>> Handle(GetItemTemplateHistoryCandidatesQuery request, CancellationToken cancellationToken)
	{
		return await candidateService.GetHistoryCandidatesAsync(request.Offset, request.Limit, request.MinCount, cancellationToken);
	}
}
