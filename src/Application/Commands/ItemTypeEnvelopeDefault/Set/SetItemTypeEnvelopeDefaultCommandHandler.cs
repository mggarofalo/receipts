using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.ItemTypeEnvelopeDefault.Set;

public class SetItemTypeEnvelopeDefaultCommandHandler(IItemTypeEnvelopeDefaultService service)
	: IRequestHandler<SetItemTypeEnvelopeDefaultCommand, Interfaces.Services.ItemTypeEnvelopeDefault>
{
	public async ValueTask<Interfaces.Services.ItemTypeEnvelopeDefault> Handle(SetItemTypeEnvelopeDefaultCommand request, CancellationToken cancellationToken)
		=> await service.SetAsync(request.CategoryId, request.EnvelopeId, cancellationToken);
}
