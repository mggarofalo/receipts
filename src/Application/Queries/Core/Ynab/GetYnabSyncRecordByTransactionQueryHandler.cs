using Application.Interfaces.Services;
using Application.Models.Ynab;
using Mediator;

namespace Application.Queries.Core.Ynab;

public class GetYnabSyncRecordByTransactionQueryHandler(
	IYnabSyncRecordService syncRecordService,
	IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<GetYnabSyncRecordByTransactionQuery, YnabSyncRecordDto?>
{
	public async ValueTask<YnabSyncRecordDto?> Handle(GetYnabSyncRecordByTransactionQuery request, CancellationToken cancellationToken)
	{
		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		return string.IsNullOrEmpty(budgetId)
			? null
			: await syncRecordService.GetByTransactionTypeAndBudgetAsync(
				request.LocalTransactionId, request.SyncType, budgetId, cancellationToken);
	}
}
