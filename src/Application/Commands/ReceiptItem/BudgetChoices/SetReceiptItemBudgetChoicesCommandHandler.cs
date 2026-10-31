using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.ReceiptItem.BudgetChoices;

public class SetReceiptItemBudgetChoicesCommandHandler(IBudgetChoiceService service) : IRequestHandler<SetReceiptItemBudgetChoicesCommand, bool>
{
	public async ValueTask<bool> Handle(SetReceiptItemBudgetChoicesCommand request, CancellationToken cancellationToken)
		=> await service.SetItemChoicesAsync(request.ItemId, request.EnvelopeId, request.OccasionId, cancellationToken);
}
