using Application.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace API.Http;

internal static class RoleChangeResponse
{
	public static Results<NoContent, NotFound, BadRequest<ProblemDetails>, Conflict<ProblemDetails>> From(RoleChangeResult result) =>
		result.Status switch
		{
			RoleChangeStatus.Success => TypedResults.NoContent(),
			RoleChangeStatus.NotFound => TypedResults.NotFound(),
			RoleChangeStatus.Invalid => ApiProblem.BadRequest(result.Errors),
			RoleChangeStatus.Conflict => ApiProblem.Conflict(string.Join(" ", result.Errors)),
			_ => throw new InvalidOperationException($"Unknown role change status: {result.Status}"),
		};
}
