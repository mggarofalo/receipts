using Application.Interfaces;
using Application.Models.Ynab;

namespace Application.Queries.Core.Ynab;

public record GetReceiptYnabSyncStatusesQuery(List<Guid> ReceiptIds) : IQuery<List<ReceiptYnabSyncStatusDto>>;
