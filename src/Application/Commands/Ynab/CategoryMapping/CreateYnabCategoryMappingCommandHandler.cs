using Application.Exceptions;
using Application.Interfaces.Services;
using Application.Models.Ynab;
using Application.Utilities;
using Mediator;

namespace Application.Commands.Ynab.CategoryMapping;

public class CreateYnabCategoryMappingCommandHandler(
	IYnabCategoryMappingService service,
	IYnabBudgetSelectionService budgetSelectionService) : IRequestHandler<CreateYnabCategoryMappingCommand, YnabCategoryMappingDto>
{
	public async ValueTask<YnabCategoryMappingDto> Handle(CreateYnabCategoryMappingCommand request, CancellationToken cancellationToken)
	{
		string budgetId = YnabDestinationId.Canonicalize(request.YnabBudgetId);
		string? selectedBudgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (!string.Equals(budgetId, selectedBudgetId, StringComparison.Ordinal))
		{
			throw new ArgumentException("The mapping budget must match the selected YNAB budget.", nameof(request));
		}

		// Cross-entity validation: check for duplicate ReceiptsCategory (case-sensitive)
		YnabCategoryMappingDto? existing = await service.GetByReceiptsCategoryAndBudgetAsync(
			request.ReceiptsCategory,
			budgetId,
			cancellationToken);
		if (existing is not null)
		{
			throw new DuplicateEntityException($"A mapping for receipts category '{request.ReceiptsCategory}' already exists.");
		}

		// CreateAsync catches DbUpdateException (unique constraint) and converts to
		// DuplicateEntityException, guarding against the TOCTOU race where two concurrent
		// requests both pass the existence check above.
		return await service.CreateAsync(
			request.ReceiptsCategory,
			request.YnabCategoryId,
			request.YnabCategoryName,
			request.YnabCategoryGroupName,
			budgetId,
			cancellationToken);
	}
}
