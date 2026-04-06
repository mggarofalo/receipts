using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Queries.Core.Ynab;

public class GetReceiptYnabSyncStatusesQueryHandler(IYnabSyncRecordService syncRecordService) : IRequestHandler<GetReceiptYnabSyncStatusesQuery, List<ReceiptYnabSyncStatusDto>>
{
	public async Task<List<ReceiptYnabSyncStatusDto>> Handle(GetReceiptYnabSyncStatusesQuery request, CancellationToken cancellationToken)
	{
		return await syncRecordService.GetSyncStatusesByReceiptIdsAsync(request.ReceiptIds, cancellationToken);
	}
}
