using Application.Interfaces.Services;
using MediatR;

namespace Application.Commands.Ynab.AccountMapping;

public class UpdateYnabAccountMappingCommandHandler(
	IYnabAccountMappingService accountMappingService) : IRequestHandler<UpdateYnabAccountMappingCommand, Unit>
{
	public async Task<Unit> Handle(UpdateYnabAccountMappingCommand request, CancellationToken cancellationToken)
	{
		await accountMappingService.UpdateAsync(
			request.Id,
			request.YnabAccountId,
			request.YnabAccountName,
			request.YnabBudgetId,
			cancellationToken);
		return Unit.Value;
	}
}
