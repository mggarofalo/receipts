using Application.Interfaces;
using Application.Models;
using Application.Models.Ynab;

namespace Application.Queries.Core.Ynab;

public record GetYnabSyncEventsQuery(
	int Offset,
	int Limit,
	SortParams Sort,
	bool? Success,
	DateTimeOffset? DateFrom,
	DateTimeOffset? DateTo) : IQuery<PagedResult<YnabSyncEventDto>>;
