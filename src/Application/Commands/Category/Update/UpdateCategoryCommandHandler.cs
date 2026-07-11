using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Category.Update;

public class UpdateCategoryCommandHandler(ICategoryService categoryService) : IRequestHandler<UpdateCategoryCommand, bool>
{
	public async ValueTask<bool> Handle(UpdateCategoryCommand request, CancellationToken cancellationToken)
	{
		foreach (Domain.Core.Category category in request.Categories)
		{
			bool exists = await categoryService.ExistsAsync(category.Id, cancellationToken);
			if (!exists)
			{
				return false;
			}
		}

		await categoryService.UpdateAsync([.. request.Categories], cancellationToken);
		return true;
	}
}
