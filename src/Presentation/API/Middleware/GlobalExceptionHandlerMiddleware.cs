using API.Http;
using Application.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace API.Middleware;

public class GlobalExceptionHandlerMiddleware(
	RequestDelegate next,
	ILogger<GlobalExceptionHandlerMiddleware> logger,
	IProblemDetailsService problemDetailsService)
{
	public async Task InvokeAsync(HttpContext context)
	{
		try
		{
			await next(context);
		}
		catch (OperationCanceledException ex) when (context.RequestAborted.IsCancellationRequested)
		{
			logger.LogDebug(
				ex,
				"Request was aborted by the client. Method: {Method}, Path: {Path}",
				context.Request.Method,
				context.Request.Path);
			if (!context.Response.HasStarted)
			{
				context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
			}
		}
		catch (Exception ex)
		{
			if (context.Response.HasStarted)
			{
				logger.LogWarning(
					ex,
					"An exception occurred after the response started. Method: {Method}, Path: {Path}",
					context.Request.Method,
					context.Request.Path);
				throw;
			}

			ProblemDetails problemDetails = BuildProblemDetails(context, ex);
			context.Response.Clear();
			context.Response.StatusCode = problemDetails.Status!.Value;
			await ApiProblemWriter.WriteAsync(
				context,
				problemDetailsService,
				problemDetails,
				context.RequestAborted);
		}
	}

	private ProblemDetails BuildProblemDetails(HttpContext context, Exception exception)
	{
		switch (exception)
		{
			case DuplicateEntityException:
				logger.LogWarning(exception, "Duplicate entity: {Message}", exception.Message);
				return CreateProblem(StatusCodes.Status409Conflict, "Conflict", "15.5.10", exception.Message);

			case KeyNotFoundException:
				logger.LogWarning(exception, "Resource not found: {Message}", exception.Message);
				return CreateProblem(StatusCodes.Status404NotFound, "Not Found", "15.5.5", exception.Message);

			case ArgumentException argumentException
				when argumentException is not ArgumentNullException and not ArgumentOutOfRangeException:
				logger.LogWarning(exception, "Validation error: {Message}", exception.Message);
				string detail = argumentException.ParamName is not null
					? argumentException.Message.Replace($" (Parameter '{argumentException.ParamName}')", "")
					: argumentException.Message;
				return new ValidationProblemDetails
				{
					Status = StatusCodes.Status400BadRequest,
					Title = "Validation Error",
					Detail = detail,
					Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
				};

			default:
				string errorId = Guid.NewGuid().ToString();
				string correlationId = context.Items[CorrelationIdMiddleware.ItemKey]?.ToString() ?? "";
				logger.LogError(
					exception,
					"Unhandled exception. ErrorId: {ErrorId}, CorrelationId: {CorrelationId}, Method: {Method}, Path: {Path}",
					errorId,
					correlationId,
					context.Request.Method,
					context.Request.Path);

				return new ProblemDetails
				{
					Status = StatusCodes.Status500InternalServerError,
					Title = "An error occurred while processing your request.",
					Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
					Extensions =
					{
						["errorId"] = errorId,
						["correlationId"] = correlationId,
					},
				};
		}
	}

	private static ProblemDetails CreateProblem(int status, string title, string section, string detail) =>
		new()
		{
			Status = status,
			Title = title,
			Detail = detail,
			Type = $"https://tools.ietf.org/html/rfc9110#section-{section}",
		};
}
