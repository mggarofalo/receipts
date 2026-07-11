using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Subcategory.Update;

public class UpdateSubcategoryCommandHandler(ISubcategoryService subcategoryService) : IRequestHandler<UpdateSubcategoryCommand, bool>
{
	public async ValueTask<bool> Handle(UpdateSubcategoryCommand request, CancellationToken cancellationToken)
	{
		foreach (Domain.Core.Subcategory subcategory in request.Subcategories)
		{
			bool exists = await subcategoryService.ExistsAsync(subcategory.Id, cancellationToken);
			if (!exists)
			{
				return false;
			}
		}

		await subcategoryService.UpdateAsync([.. request.Subcategories], cancellationToken);
		return true;
	}
}
