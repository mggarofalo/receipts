using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Mediator;

namespace Application.Commands.NormalizedDescription.Split;

public class SplitNormalizedDescriptionCommandHandler(INormalizedDescriptionService service)
	: IRequestHandler<SplitNormalizedDescriptionCommand, NormalizedDescriptionDetail>
{
	public async ValueTask<NormalizedDescriptionDetail> Handle(SplitNormalizedDescriptionCommand request, CancellationToken cancellationToken)
	{
		return await service.SplitAsync(request.ReceiptItemId, cancellationToken);
	}
}
