using Application.Interfaces;
using Application.Models.Ynab;
using Common;

namespace Application.Queries.Core.Ynab;

public record GetYnabSyncRecordByTransactionQuery(Guid LocalTransactionId, YnabSyncType SyncType) : IQuery<YnabSyncRecordDto?>;
