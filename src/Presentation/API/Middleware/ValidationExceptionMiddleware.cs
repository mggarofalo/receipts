using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace API.Middleware;

public class ValidationExceptionMiddleware(RequestDelegate next)
{
	public async Task InvokeAsync(HttpContext context)
	{
		try
		{
			await next(context);
		}
		catch (ValidationException ex)
		{
			context.Response.StatusCode = StatusCodes.Status400BadRequest;

			Dictionary<string, string[]> errors = ex.Errors
				.GroupBy(e => e.PropertyName)
				.ToDictionary(
					g => g.Key,
					g => g.Select(e => e.ErrorMessage).ToArray());

			ValidationProblemDetails problemDetails = ApiValidationProblem.Create(errors);

			await context.Response.WriteAsJsonAsync(
				problemDetails,
				options: null,
				contentType: "application/problem+json");
		}
	}
}
