using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Adjustment.Update;

public class UpdateAdjustmentCommandHandler(IAdjustmentService adjustmentService) : IRequestHandler<UpdateAdjustmentCommand, bool>
{
	public async ValueTask<bool> Handle(UpdateAdjustmentCommand request, CancellationToken cancellationToken)
	{
		Domain.Core.Adjustment? existingAdjustment = await adjustmentService.GetByIdAsync(request.Adjustments[0].Id, cancellationToken);
		if (existingAdjustment is null)
		{
			return false;
		}

		await adjustmentService.UpdateAsync([.. request.Adjustments], existingAdjustment.ReceiptId, cancellationToken);
		return true;
	}
}
