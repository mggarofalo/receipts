using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Queries.Core.Ynab;

public class GetYnabSyncRecordByTransactionQueryHandler(IYnabSyncRecordService syncRecordService) : IRequestHandler<GetYnabSyncRecordByTransactionQuery, YnabSyncRecordDto?>
{
	public async Task<YnabSyncRecordDto?> Handle(GetYnabSyncRecordByTransactionQuery request, CancellationToken cancellationToken)
	{
		return await syncRecordService.GetByTransactionAndTypeAsync(request.LocalTransactionId, request.SyncType, cancellationToken);
	}
}
