using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.ReceiptItem.EnvelopeAutomatic;

public class SetReceiptItemEnvelopeAutomaticCommandHandler(IBudgetChoiceService service)
	: IRequestHandler<SetReceiptItemEnvelopeAutomaticCommand, ItemEnvelopeAutomaticResult>
{
	public async ValueTask<ItemEnvelopeAutomaticResult> Handle(SetReceiptItemEnvelopeAutomaticCommand request, CancellationToken cancellationToken)
		=> await service.SetItemAutomaticAsync(request.ItemId, cancellationToken);
}
