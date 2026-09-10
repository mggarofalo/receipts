using Microsoft.AspNetCore.Mvc;

namespace API.Http;

/// <summary>
/// Writes the API's RFC problem contract even when content negotiation rejects ASP.NET's
/// default JSON writer. Error responses must not fail a second time because a caller sent
/// an unsupported <c>Accept</c> header.
/// </summary>
public static class ApiProblemWriter
{
	public static async ValueTask WriteAsync(
		HttpContext context,
		IProblemDetailsService problemDetailsService,
		ProblemDetails problemDetails,
		CancellationToken cancellationToken = default)
	{
		ProblemDetailsContext problemContext = new()
		{
			HttpContext = context,
			ProblemDetails = problemDetails,
		};

		if (await problemDetailsService.TryWriteAsync(problemContext))
		{
			return;
		}

		await context.Response.WriteAsJsonAsync(
			problemDetails,
			problemDetails.GetType(),
			options: null,
			contentType: "application/problem+json",
			cancellationToken);
	}
}
