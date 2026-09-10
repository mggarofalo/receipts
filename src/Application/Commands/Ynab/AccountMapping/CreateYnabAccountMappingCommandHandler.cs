using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Utilities;
using Mediator;

namespace Application.Commands.Ynab.AccountMapping;

public class CreateYnabAccountMappingCommandHandler(
	IYnabAccountMappingService accountMappingService,
	IAccountService accountService,
	IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<CreateYnabAccountMappingCommand, YnabAccountMappingDto>
{
	public async ValueTask<YnabAccountMappingDto> Handle(CreateYnabAccountMappingCommand request, CancellationToken cancellationToken)
	{
		string budgetId = YnabDestinationId.Canonicalize(request.YnabBudgetId);
		string? selectedBudgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (!string.Equals(budgetId, selectedBudgetId, StringComparison.Ordinal))
		{
			throw new ArgumentException("The mapping budget must match the selected YNAB budget.", nameof(request));
		}

		// ReceiptsAccountId is an FK to Accounts (see YnabAccountMappingEntityConfiguration),
		// so validate existence against Accounts — not Cards (RECEIPTS-751). Validating against
		// Cards only worked for legacy data where each Card shared its Account's Guid, and
		// rejected any Account created after the account/card split.
		bool accountExists = await accountService.ExistsAsync(request.ReceiptsAccountId, cancellationToken);
		if (!accountExists)
		{
			throw new ArgumentException($"Account with ID '{request.ReceiptsAccountId}' does not exist.", nameof(request));
		}

		return await accountMappingService.CreateAsync(
			request.ReceiptsAccountId,
			request.YnabAccountId,
			request.YnabAccountName,
			budgetId,
			cancellationToken);
	}
}
