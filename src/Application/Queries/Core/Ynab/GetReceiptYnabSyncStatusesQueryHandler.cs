using Application.Interfaces.Services;
using Application.Models.Ynab;
using Mediator;

namespace Application.Queries.Core.Ynab;

public class GetReceiptYnabSyncStatusesQueryHandler(
	IYnabSyncRecordService syncRecordService,
	IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<GetReceiptYnabSyncStatusesQuery, List<ReceiptYnabSyncStatusDto>>
{
	public async ValueTask<List<ReceiptYnabSyncStatusDto>> Handle(GetReceiptYnabSyncStatusesQuery request, CancellationToken cancellationToken)
	{
		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		return string.IsNullOrEmpty(budgetId)
			? request.ReceiptIds.Select(id => new ReceiptYnabSyncStatusDto(id, ReceiptSyncStatusValue.NotSynced)).ToList()
			: await syncRecordService.GetSyncStatusesByReceiptIdsAndBudgetAsync(
				request.ReceiptIds, budgetId, cancellationToken);
	}
}
