using System.Reflection;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace API.Http;

/// <summary>A reasoned 409 whose status, schema, and problem media type survive OpenAPI generation.</summary>
public sealed class ConflictProblem : IResult, IEndpointMetadataProvider, IStatusCodeHttpResult, IValueHttpResult, IValueHttpResult<ProblemDetails>
{
	internal ConflictProblem(ProblemDetails value)
	{
		Value = value;
		Value.Status = StatusCode;
	}

	public ProblemDetails Value { get; }
	object? IValueHttpResult.Value => Value;
	public int StatusCode => 409;
	int? IStatusCodeHttpResult.StatusCode => StatusCode;

	public Task ExecuteAsync(HttpContext context)
	{
		Value.Status = StatusCode;
		return TypedResults.Problem(Value).ExecuteAsync(context);
	}

	public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder) =>
		builder.Metadata.Add(new ProducesResponseTypeMetadata(409, typeof(ProblemDetails), ["application/problem+json"]));
}
