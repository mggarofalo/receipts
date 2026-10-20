using System.Reflection;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace API.Http;

/// <summary>A reasoned 403 whose status and schema survive OpenAPI generation.</summary>
public sealed class ForbiddenProblem(string detail, string code) : IResult, IEndpointMetadataProvider
{
	public Task ExecuteAsync(HttpContext context) => TypedResults.Problem(new ProblemDetails
	{
		Status = 403,
		Title = "Forbidden",
		Detail = detail,
		Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
		Extensions = { ["code"] = code },
	}).ExecuteAsync(context);

	public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder) =>
		builder.Metadata.Add(new ProducesResponseTypeAttribute(typeof(ProblemDetails), 403, "application/problem+json"));
}
