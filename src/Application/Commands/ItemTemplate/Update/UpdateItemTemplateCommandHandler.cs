using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.ItemTemplate.Update;

public class UpdateItemTemplateCommandHandler(IItemTemplateService itemTemplateService) : IRequestHandler<UpdateItemTemplateCommand, bool>
{
	public async ValueTask<bool> Handle(UpdateItemTemplateCommand request, CancellationToken cancellationToken)
	{
		foreach (Domain.Core.ItemTemplate itemTemplate in request.ItemTemplates)
		{
			bool exists = await itemTemplateService.ExistsAsync(itemTemplate.Id, cancellationToken);
			if (!exists)
			{
				return false;
			}
		}

		await itemTemplateService.UpdateAsync([.. request.ItemTemplates], cancellationToken);
		return true;
	}
}
