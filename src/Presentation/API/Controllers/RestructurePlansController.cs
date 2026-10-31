using System.Security.Claims;
using API.Http;
using Application.Taxonomy;
using Application.Taxonomy.Restructure;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

/// <summary>
/// Validates, dry-runs, applies and undoes category restructure plans. Administrators only: a plan rewrites the
/// category tree and reclassifies purchases for everyone.
/// </summary>
[ApiVersion("1.0")]
[ApiController]
[Authorize(Policy = "RequireAdmin")]
[Route("api/restructure-plans")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class RestructurePlansController(IRestructurePlanService service) : ControllerBase
{
	private const int MaximumChunk = HistoricalReclassificationPolicy.MaximumChunkSize;

	[HttpPost("preview")]
	[EndpointSummary("Validate a restructure plan and report what applying it would do")]
	public async Task<Results<Ok<RestructurePreview>, BadRequestProblem>> Preview([FromBody] RestructurePlan plan, CancellationToken ct)
	{
		if (plan is null) { return ApiProblem.BadRequest("A restructure plan is required."); }
		return TypedResults.Ok(await service.PreviewAsync(plan, ct));
	}

	[HttpPost]
	[EndpointSummary("Apply a restructure plan's category changes and capture its purchase reassignments")]
	public Task<Results<Ok<RestructureApplicationSummary>, BadRequestProblem, NotFoundProblem, ConflictProblem>> Begin(
		[FromBody] RestructureBeginBody body, CancellationToken ct) => Run(async () =>
		body.ApplicationId == Guid.Empty || body.Plan is null
			? ApiProblem.BadRequest("An application ID and a restructure plan are required.")
			: TypedResults.Ok(await service.BeginAsync(Actor(), body.ApplicationId, body.Plan, ct)));

	[HttpGet]
	[EndpointSummary("List restructure applications, newest first")]
	public async Task<Results<Ok<RestructureApplicationPage>, BadRequestProblem>> List(
		[FromQuery] int offset = 0, [FromQuery] int limit = 25, CancellationToken ct = default)
	{
		if (offset < 0 || limit is < 1 or > 25) { return ApiProblem.BadRequest("Use a nonnegative offset and a limit of 1 to 25."); }
		return TypedResults.Ok(await service.ListAsync(offset, limit, ct));
	}

	[HttpGet("{id:guid}")]
	[EndpointSummary("Read a restructure application's progress and report")]
	public Task<Results<Ok<RestructureApplicationSummary>, BadRequestProblem, NotFoundProblem, ConflictProblem>> Read(
		Guid id, CancellationToken ct) => Run(async () => TypedResults.Ok(await service.ReadAsync(id, ct)));

	[HttpGet("{id:guid}/items")]
	[EndpointSummary("Page a restructure application's reassigned purchases")]
	public async Task<Results<Ok<RestructureItemPage>, BadRequestProblem, NotFoundProblem>> Items(Guid id,
		[FromQuery] RestructureItemFilter filter = RestructureItemFilter.All, [FromQuery] int offset = 0,
		[FromQuery] int limit = MaximumChunk, CancellationToken ct = default)
	{
		if (!Enum.IsDefined(filter) || offset < 0 || limit is < 1 or > MaximumChunk)
		{ return ApiProblem.BadRequest("Use a known filter, a nonnegative offset and a limit of 1 to 100."); }
		try { return TypedResults.Ok(await service.ReadItemsAsync(id, filter, offset, limit, ct)); }
		catch (TaxonomyException error) when (error.Status == 404) { return ApiProblem.NotFound(error.Message); }
	}

	[HttpPost("{id:guid}/apply")]
	[EndpointSummary("Reassign the next chunk of purchases")]
	public Task<Results<Ok<RestructureApplicationSummary>, BadRequestProblem, NotFoundProblem, ConflictProblem>> Apply(
		Guid id, [FromBody] RestructureChunkBody body, CancellationToken ct) => Run(async () =>
		body.Limit is < 1 or > MaximumChunk
			? ApiProblem.BadRequest("Use a chunk limit of 1 to 100.")
			: TypedResults.Ok(await service.ApplyChunkAsync(Actor(), id, body.Limit, ct)));

	[HttpPost("{id:guid}/cancel")]
	[EndpointSummary("Stop reassigning purchases; reassigned purchases stay until undone")]
	public Task<Results<Ok<RestructureApplicationSummary>, BadRequestProblem, NotFoundProblem, ConflictProblem>> Cancel(
		Guid id, CancellationToken ct) => Run(async () => TypedResults.Ok(await service.CancelAsync(Actor(), id, ct)));

	[HttpPost("{id:guid}/undo")]
	[EndpointSummary("Undo a restructure: categories first, then the next chunk of purchases")]
	public Task<Results<Ok<RestructureApplicationSummary>, BadRequestProblem, NotFoundProblem, ConflictProblem>> Undo(
		Guid id, [FromBody] RestructureChunkBody body, CancellationToken ct) => Run(async () =>
		body.Limit is < 1 or > MaximumChunk
			? ApiProblem.BadRequest("Use a chunk limit of 1 to 100.")
			: TypedResults.Ok(await service.UndoChunkAsync(Actor(), id, body.Limit, ct)));

	private string Actor() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

	private static async Task<Results<Ok<RestructureApplicationSummary>, BadRequestProblem, NotFoundProblem, ConflictProblem>> Run(
		Func<Task<Results<Ok<RestructureApplicationSummary>, BadRequestProblem, NotFoundProblem, ConflictProblem>>> action)
	{
		try { return await action(); }
		catch (TaxonomyException error)
		{
			return error.Status switch
			{
				404 => ApiProblem.NotFound(error.Message),
				409 => ApiProblem.Conflict(error.Message, error.Extensions),
				_ when error.Extensions.TryGetValue("errors", out object? errors) && errors is IEnumerable<string> details
					=> ApiProblem.BadRequest(details),
				_ => ApiProblem.BadRequest(error.Message)
			};
		}
		catch (ArgumentException error) { return ApiProblem.BadRequest(error.Message); }
		catch (InvalidOperationException error) { return ApiProblem.Conflict(error.Message); }
	}
}

public sealed record RestructureBeginBody(Guid ApplicationId, RestructurePlan Plan);
public sealed record RestructureChunkBody(int Limit);
