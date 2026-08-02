using Application.Interfaces;
using Application.Models;

namespace Application.Queries.Core.ItemTemplate.GetHistoryCandidates;

public record GetItemTemplateHistoryCandidatesQuery(int Offset, int Limit, int MinCount)
	: IQuery<PagedResult<ItemTemplateHistoryCandidate>>;
