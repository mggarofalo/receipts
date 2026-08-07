using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Mediator;

namespace Application.Commands.NormalizedDescription.Rename;

public class RenameNormalizedDescriptionCommandHandler(INormalizedDescriptionService service)
	: IRequestHandler<RenameNormalizedDescriptionCommand, NormalizedDescriptionDetail>
{
	public async ValueTask<NormalizedDescriptionDetail> Handle(RenameNormalizedDescriptionCommand request, CancellationToken cancellationToken)
	{
		return await service.RenameAsync(request.Id, request.DisplayLabel, cancellationToken);
	}
}
