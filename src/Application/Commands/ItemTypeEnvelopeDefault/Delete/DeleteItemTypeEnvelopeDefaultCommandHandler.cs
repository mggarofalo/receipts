using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.ItemTypeEnvelopeDefault.Delete;

public class DeleteItemTypeEnvelopeDefaultCommandHandler(IItemTypeEnvelopeDefaultService service)
	: IRequestHandler<DeleteItemTypeEnvelopeDefaultCommand, bool>
{
	public async ValueTask<bool> Handle(DeleteItemTypeEnvelopeDefaultCommand request, CancellationToken cancellationToken)
		=> await service.DeleteAsync(request.CategoryId, cancellationToken);
}
