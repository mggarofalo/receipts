using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc.Filters;

namespace API.Filters;

public class FluentValidationActionFilter(IServiceProvider serviceProvider) : IAsyncActionFilter
{
	public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
	{
		List<ValidationFailure> failures = [];
		foreach (object? argument in context.ActionArguments.Values)
		{
			if (argument is null)
			{
				continue;
			}

			await ValidateAsync(argument, "", failures, context.HttpContext.RequestAborted);
		}

		// Validate the complete body before invoking any action or persistence.
		context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
		if (failures.Count > 0)
		{
			throw new ValidationException(failures);
		}

		await next();
	}

	private async Task ValidateAsync(object? value, string path, List<ValidationFailure> failures, CancellationToken cancellationToken)
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
			if (list.Count == 0)
			{
				failures.Add(new ValidationFailure(path, "Request body must contain at least one item."));
			}
			for (int index = 0; index < list.Count; index++)
			{
				await ValidateAsync(list[index], $"{path}[{index}]", failures, cancellationToken);
			}
		}
	}
}
