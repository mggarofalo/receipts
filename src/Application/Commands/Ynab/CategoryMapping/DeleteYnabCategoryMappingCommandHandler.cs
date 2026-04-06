using Application.Interfaces.Services;
using MediatR;

namespace Application.Commands.Ynab.CategoryMapping;

public class DeleteYnabCategoryMappingCommandHandler(IYnabCategoryMappingService service) : IRequestHandler<DeleteYnabCategoryMappingCommand, Unit>
{
	public async Task<Unit> Handle(DeleteYnabCategoryMappingCommand request, CancellationToken cancellationToken)
	{
		await service.DeleteAsync(request.Id, cancellationToken);
		return Unit.Value;
	}
}
