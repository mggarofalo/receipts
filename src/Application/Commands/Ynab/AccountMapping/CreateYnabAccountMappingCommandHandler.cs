using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Commands.Ynab.AccountMapping;

public class CreateYnabAccountMappingCommandHandler(
	IYnabAccountMappingService accountMappingService,
	IAccountService accountService) : IRequestHandler<CreateYnabAccountMappingCommand, YnabAccountMappingDto>
{
	public async Task<YnabAccountMappingDto> Handle(CreateYnabAccountMappingCommand request, CancellationToken cancellationToken)
	{
		bool accountExists = await accountService.ExistsAsync(request.ReceiptsAccountId, cancellationToken);
		if (!accountExists)
		{
			throw new InvalidOperationException($"Account with ID '{request.ReceiptsAccountId}' does not exist.");
		}

		return await accountMappingService.CreateAsync(
			request.ReceiptsAccountId,
			request.YnabAccountId,
			request.YnabAccountName,
			request.YnabBudgetId,
			cancellationToken);
	}
}
