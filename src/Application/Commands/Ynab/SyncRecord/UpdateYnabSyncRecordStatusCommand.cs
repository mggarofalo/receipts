using Application.Interfaces;
using Common;
using MediatR;

namespace Application.Commands.Ynab.SyncRecord;

public record UpdateYnabSyncRecordStatusCommand(Guid Id, YnabSyncStatus Status, string? YnabTransactionId, string? LastError) : ICommand<Unit>;
