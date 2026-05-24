using Application.Interfaces;
using Application.Models.Ynab;
using Common;

namespace Application.Queries.Core.Ynab;

public record GetYnabSyncEventsQuery(int Offset, int Limit, YnabSyncStatus? Outcome) : IQuery<YnabSyncEventsPage>;
