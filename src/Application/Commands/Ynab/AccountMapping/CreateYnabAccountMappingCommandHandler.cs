using Application.Interfaces.Services;
using Application.Models.Ynab;
using Mediator;

namespace Application.Commands.Ynab.AccountMapping;

public class CreateYnabAccountMappingCommandHandler(
	IYnabAccountMappingService accountMappingService,
	IAccountService accountService) : IRequestHandler<CreateYnabAccountMappingCommand, YnabAccountMappingDto>
{
	public async ValueTask<YnabAccountMappingDto> Handle(CreateYnabAccountMappingCommand request, CancellationToken cancellationToken)
	{
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
			request.YnabBudgetId,
			cancellationToken);
	}
}
