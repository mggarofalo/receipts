using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace API.Http;

public static class ApiValidationProblem
{
	public static ValidationProblemDetails Create(IDictionary<string, string[]> errors) => new(errors)
	{
		Status = StatusCodes.Status400BadRequest,
		Title = "One or more validation errors occurred.",
		Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
		Detail = string.Join(" ", errors.Values.SelectMany(messages => messages).Distinct(StringComparer.Ordinal)),
	};

	public static JsonResult FromModelState(ModelStateDictionary modelState)
	{
		Dictionary<string, string[]> errors = modelState
			.Where(entry => entry.Value is { Errors.Count: > 0 })
			.ToDictionary(entry => entry.Key, entry => entry.Value!.Errors
				.Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
					? "The supplied value is invalid." : error.ErrorMessage)
				.ToArray());
		// JsonResult preserves the problem media type even on controllers with
		// [Produces("application/json")], which overrides ObjectResult formats.
		return new JsonResult(Create(errors))
		{
			StatusCode = StatusCodes.Status400BadRequest,
			ContentType = "application/problem+json",
		};
	}
}
