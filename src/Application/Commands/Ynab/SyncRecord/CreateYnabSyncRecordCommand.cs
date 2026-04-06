using Application.Interfaces;
using Application.Models.Ynab;
using Common;

namespace Application.Commands.Ynab.SyncRecord;

public record CreateYnabSyncRecordCommand(Guid LocalTransactionId, string YnabBudgetId, YnabSyncType SyncType) : ICommand<YnabSyncRecordDto>;
