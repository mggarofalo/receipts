using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Receipt.BudgetChoices;

public class SetReceiptBudgetChoicesCommandHandler(IBudgetChoiceService service) : IRequestHandler<SetReceiptBudgetChoicesCommand, bool>
{
	public async ValueTask<bool> Handle(SetReceiptBudgetChoicesCommand request, CancellationToken cancellationToken)
		=> await service.SetReceiptChoicesAsync(request.ReceiptId, request.PaidFromEnvelopeId, request.OccasionId, cancellationToken);
}
