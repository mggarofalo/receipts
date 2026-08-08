using Application.Interfaces.Services;
using Application.Models.NormalizedDescriptions;
using Mediator;

namespace Application.Commands.NormalizedDescription.LinkTemplate;

public class LinkItemTemplateCommandHandler(INormalizedDescriptionService service)
	: IRequestHandler<LinkItemTemplateCommand, LinkTemplateResult>
{
	public async ValueTask<LinkTemplateResult> Handle(LinkItemTemplateCommand request, CancellationToken cancellationToken)
	{
		return await service.LinkTemplateAsync(request.DescriptionId, request.ItemTemplateId, cancellationToken);
	}
}
