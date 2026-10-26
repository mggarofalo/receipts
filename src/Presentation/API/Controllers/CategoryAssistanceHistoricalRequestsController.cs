using System.Security.Claims;
using API.Http;
using Application.Classification;
using Application.Taxonomy;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Authorize]
[Route("api/category-assistance/historical-requests")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class CategoryAssistanceHistoricalRequestsController(
	ICategoryAssistanceHistoricalRequestStore store) : ControllerBase
{
	[HttpPost]
	[EndpointSummary("Enroll selected exact historical groups for optional category assistance")]
	public async Task<Results<Ok<CategoryAssistanceHistoricalProgress>, BadRequestProblem,
		ForbiddenProblem, ConflictProblem>> Enroll([FromBody] CategoryAssistanceHistoricalRequest request,
		CancellationToken ct)
	{
		try { return TypedResults.Ok(await store.EnrollAsync(Actor(), request, ct)); }
		catch (TaxonomyException error)
		{
			return error.Status switch
			{
				403 => new ForbiddenProblem(error.Message, "writer_required"),
				409 => ApiProblem.Conflict(error.Message, error.Extensions),
				_ => ApiProblem.BadRequest(error.Message)
			};
		}
	}

	[HttpGet("{operationId:guid}")]
	[EndpointSummary("Read own historical assistance enrollment progress")]
	public async Task<Results<Ok<CategoryAssistanceHistoricalProgress>, BadRequestProblem,
		NotFoundProblem>> Get(Guid operationId, CancellationToken ct)
	{
		if (operationId == Guid.Empty)
		{
			return ApiProblem.BadRequest("Operation ID is required.");
		}

		try { return TypedResults.Ok(await store.GetAsync(Actor(), operationId, ct)); }
		catch (TaxonomyException) { return ApiProblem.NotFound("Historical assistance request does not exist."); }
	}

	[HttpGet]
	[EndpointSummary("List own recent historical assistance requests")]
	public async Task<Results<Ok<CategoryAssistanceHistoricalPage>, BadRequestProblem>> List(
		[FromQuery] int offset = 0, [FromQuery] int limit = 25, CancellationToken ct = default)
	{
		if (offset < 0 || limit is < 1 or > 25)
		{
			return ApiProblem.BadRequest("Use a nonnegative offset and limit of 1 to 25.");
		}

		return TypedResults.Ok(await store.ListAsync(Actor(), offset, limit, ct));
	}

	private string Actor() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
}
