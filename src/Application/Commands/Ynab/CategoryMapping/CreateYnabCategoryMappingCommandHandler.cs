using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Commands.Ynab.CategoryMapping;

public class CreateYnabCategoryMappingCommandHandler(IYnabCategoryMappingService service) : IRequestHandler<CreateYnabCategoryMappingCommand, YnabCategoryMappingDto>
{
	public async Task<YnabCategoryMappingDto> Handle(CreateYnabCategoryMappingCommand request, CancellationToken cancellationToken)
	{
		// Cross-entity validation: check for duplicate ReceiptsCategory (case-sensitive)
		YnabCategoryMappingDto? existing = await service.GetByReceiptsCategoryAsync(request.ReceiptsCategory, cancellationToken);
		if (existing is not null)
		{
			throw new InvalidOperationException($"A mapping for receipts category '{request.ReceiptsCategory}' already exists.");
		}

		return await service.CreateAsync(
			request.ReceiptsCategory,
			request.YnabCategoryId,
			request.YnabCategoryName,
			request.YnabCategoryGroupName,
			request.YnabBudgetId,
			cancellationToken);
	}
}
