using Application.Interfaces.Services;
using MediatR;

namespace Application.Commands.Ynab.CategoryMapping;

public class UpdateYnabCategoryMappingCommandHandler(IYnabCategoryMappingService service) : IRequestHandler<UpdateYnabCategoryMappingCommand, Unit>
{
	public async Task<Unit> Handle(UpdateYnabCategoryMappingCommand request, CancellationToken cancellationToken)
	{
		await service.UpdateAsync(
			request.Id,
			request.YnabCategoryId,
			request.YnabCategoryName,
			request.YnabCategoryGroupName,
			request.YnabBudgetId,
			cancellationToken);

		return Unit.Value;
	}
}
