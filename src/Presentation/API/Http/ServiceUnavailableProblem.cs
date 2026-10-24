using System.Reflection;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace API.Http;

/// <summary>A reasoned 503 whose status and schema survive OpenAPI generation.</summary>
public sealed class ServiceUnavailableProblem(string detail, string code) : IResult, IEndpointMetadataProvider
{
	public Task ExecuteAsync(HttpContext context) => TypedResults.Problem(new ProblemDetails
	{
		Status = 503,
		Title = "Service Unavailable",
		Detail = detail,
		Type = "https://tools.ietf.org/html/rfc9110#section-15.6.4",
		Extensions = { ["code"] = code },
	}).ExecuteAsync(context);

	public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder) =>
		builder.Metadata.Add(new ProducesResponseTypeMetadata(503, typeof(ProblemDetails), ["application/problem+json"]));
}
