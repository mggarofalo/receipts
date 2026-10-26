using API.Generated.Dtos;
using FluentValidation;

namespace API.Validators;

public sealed class CategoryMappingChangeRequestValidator : AbstractValidator<CategoryMappingChangeRequest>
{
	public CategoryMappingChangeRequestValidator()
	{
		RuleFor(request => request.OperationId).NotEmpty();
		RuleFor(request => request.ExpectedTaxonomyRevisionId).NotEmpty();
		RuleFor(request => request.ExpectedMappingRevisionId).Must(id => id != Guid.Empty)
			.WithMessage("Expected mapping revision must be null or a nonempty revision ID.");
		RuleFor(request => request.Category).Must(category => category == null || category.Id != Guid.Empty)
			.WithMessage("Category must be null or identify a category.");
		RuleFor(request => request.Reason).NotEmpty().MaximumLength(4000)
			.Must(reason => reason == null || !reason.Contains('\0')).WithMessage("Reason must not contain NUL.");
	}
}
