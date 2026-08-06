using Application.Interfaces.Services;
using Application.Models.Merge;
using Mediator;

namespace Application.Queries.Core.Card;

public class PreviewMergeCardsQueryHandler(IAccountMergeService mergeService)
	: IRequestHandler<PreviewMergeCardsQuery, MergeCardsPreview>
{
	public async ValueTask<MergeCardsPreview> Handle(PreviewMergeCardsQuery request, CancellationToken cancellationToken)
	{
		return await mergeService.PreviewMergeCardsAsync(
			request.TargetAccountId,
			request.SourceCardIds,
			request.YnabMappingWinnerAccountId,
			cancellationToken);
	}
}
