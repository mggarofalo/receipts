using Application.Interfaces.Services;
using MediatR;

namespace Application.Commands.Ynab.SyncRecord;

public class UpdateYnabSyncRecordStatusCommandHandler(IYnabSyncRecordService syncRecordService) : IRequestHandler<UpdateYnabSyncRecordStatusCommand, Unit>
{
	public async Task<Unit> Handle(UpdateYnabSyncRecordStatusCommand request, CancellationToken cancellationToken)
	{
		await syncRecordService.UpdateStatusAsync(request.Id, request.Status, request.YnabTransactionId, request.LastError, cancellationToken);
		return Unit.Value;
	}
}
