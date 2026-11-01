using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace API.Filters;

public class FluentValidationActionFilter(IServiceProvider serviceProvider) : IAsyncActionFilter
{
	public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
	{
		List<ValidationFailure> failures = [];
		foreach ((string name, object? argument) in context.ActionArguments)
		{
			if (argument is null)
			{
				continue;
			}

			await ValidateAsync(argument, "", RequiresItems(context, name), failures, context.HttpContext.RequestAborted);
		}

		// Validate the complete body before invoking any action or persistence.
		context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
		if (failures.Count > 0)
		{
			throw new ValidationException(failures);
		}

		await next();
	}

	// The non-empty rule guards batch request bodies. A repeatable query parameter that is absent
	// binds as an empty array and means "no filter", so it is not a missing body (RECEIPTS-1108).
	private static bool RequiresItems(ActionExecutingContext context, string name) =>
		context.ActionDescriptor.Parameters.SingleOrDefault(parameter => parameter.Name == name)?.BindingInfo?.BindingSource is not { } source
		|| source == BindingSource.Body;

	private async Task ValidateAsync(object? value, string path, bool requiresItems, List<ValidationFailure> failures, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (value is null)
		{
			failures.Add(new ValidationFailure(path, "Request items must not be null."));
			return;
		}

		Type validatorType = typeof(IValidator<>).MakeGenericType(value.GetType());
		if (serviceProvider.GetService(validatorType) is IValidator validator)
		{
			IValidationContext validationContext = new ValidationContext<object>(value);
			ValidationResult result = await validator.ValidateAsync(validationContext, cancellationToken);
			foreach (ValidationFailure failure in result.Errors)
			{
				string propertyName = string.IsNullOrEmpty(path) ? failure.PropertyName
					: string.IsNullOrEmpty(failure.PropertyName) ? path
					: $"{path}.{failure.PropertyName}";
				failures.Add(new ValidationFailure(propertyName, failure.ErrorMessage, failure.AttemptedValue)
				{
					ErrorCode = failure.ErrorCode,
					Severity = failure.Severity,
					CustomState = failure.CustomState,
					FormattedMessagePlaceholderValues = failure.FormattedMessagePlaceholderValues,
				});
			}
		}

		// Collection rules and element rules have separate owners. A registered
		// list validator does not replace the DTO validator for each item.
		if (value is System.Collections.IList list)
		{
			if (requiresItems && list.Count == 0)
			{
				failures.Add(new ValidationFailure(path, "Request body must contain at least one item."));
			}
			for (int index = 0; index < list.Count; index++)
			{
				await ValidateAsync(list[index], $"{path}[{index}]", requiresItems, failures, cancellationToken);
			}
		}
	}
}
