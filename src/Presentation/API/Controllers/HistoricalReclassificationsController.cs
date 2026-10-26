using System.Security.Claims;
using API.Http;
using Application.Taxonomy;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Authorize]
[Route("api/historical-reclassifications")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class HistoricalReclassificationsController(IHistoricalOperationService service,
	IHistoricalGroupReader groups) : ControllerBase
{
	[HttpGet("groups")]
	[EndpointSummary("Browse live exact-source historical purchase groups")]
	public async Task<Results<Ok<HistoricalGroupPage>, BadRequestProblem>> Groups(
		[FromQuery] int offset = 0, [FromQuery] int limit = 25, [FromQuery(Name = "q")] string? search = null,
		[FromQuery] bool conflictsOnly = false, [FromQuery] bool migratedBroadOnly = false,
		[FromQuery] bool unresolvedOnly = false, [FromQuery] bool unassignedOnly = false,
								[FromQuery] HistoricalDispositionFilter dispositionFilter = HistoricalDispositionFilter.Visible,
				[FromQuery] string sort = "mostPurchases", [FromQuery] string impactCurrency = "USD",
		CancellationToken ct = default)
	{
		if (offset < 0 || limit is < 1 or > 25 || search is { Length: > 200 } || search?.Contains('\0') == true ||
								!Enum.IsDefined(dispositionFilter) ||
								sort is not ("mostPurchases" or "largestAmount") || impactCurrency != "USD")
		{ return ApiProblem.BadRequest("Use a nonnegative offset, limit of 1 to 25, search of at most 200 characters, a supported sort and impact currency (USD)."); }
		return TypedResults.Ok(await groups.BrowseAsync(new(offset, limit, search,
												conflictsOnly, migratedBroadOnly, unresolvedOnly, unassignedOnly, Actor(), dispositionFilter,
												sort, impactCurrency), ct));
	}

	[HttpGet]
	[EndpointSummary("List own recent historical operations for resumption")]
	public Task<Results<Ok<HistoricalOperationPage>, BadRequestProblem>> List(
			[FromQuery] int offset = 0, [FromQuery] int limit = 25, CancellationToken ct = default) =>
			offset < 0 || limit is < 1 or > 25
					? Task.FromResult<Results<Ok<HistoricalOperationPage>, BadRequestProblem>>(
							ApiProblem.BadRequest("Use a nonnegative offset and limit of 1 to 25."))
					: ListValid(offset, limit, ct);

	private async Task<Results<Ok<HistoricalOperationPage>, BadRequestProblem>> ListValid(
			int offset, int limit, CancellationToken ct) =>
			TypedResults.Ok(await service.ListAsync(Actor(), offset, limit, ct));

	[HttpPut("groups/disposition")]
	[EndpointSummary("Set own exact-source review disposition with compare-and-set version")]
	public async Task<Results<Ok<HistoricalDispositionRecord>, BadRequestProblem, ConflictProblem>> SetDisposition(
			[FromBody] HistoricalDispositionRequest body, CancellationToken ct)
	{
		try { return TypedResults.Ok(await service.SetDispositionAsync(Actor(), body, ct)); }
		catch (TaxonomyException error)
		{
			return error.Status == 409 ? ApiProblem.Conflict(error.Message) : ApiProblem.BadRequest(error.Message);
		}
	}

	[HttpPost]
	[EndpointSummary("Capture an exact historical classification preview")]
	public Task<Results<Ok<HistoricalProgress>, BadRequestProblem, NotFoundProblem, ConflictProblem>> Preview(
			[FromBody] HistoricalPreviewBody body, CancellationToken ct) => Run(async () =>
	{
		if (body.OperationId == Guid.Empty || body.TaxonomyRevisionId == Guid.Empty ||
					string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Contains('\0') ||
					body.Groups is not { Count: >= 1 and <= HistoricalReclassificationPolicy.MaximumSelectedGroups } ||
					body.Groups.Any(x => x == null || x.Group == null ||
							string.IsNullOrWhiteSpace(x.Group.Store) || string.IsNullOrWhiteSpace(x.Group.RawDescription) ||
							x.Group.Store.Contains('\0') || x.Group.RawDescription.Contains('\0') ||
							x.Group.ItemCode?.Contains('\0') == true || x.TargetCategoryId == Guid.Empty))
		{ return ApiProblem.BadRequest("Provide an operation ID, taxonomy revision, reason and one to 25 valid exact source groups."); }
		return TypedResults.Ok(await service.SavePreviewAsync(new(body.OperationId, Actor(), body.TaxonomyRevisionId,
					body.Reason, body.Groups.Select(x => new HistoricalGroupSelection(
							new HistoricalGroupKey(x.Group.Store, x.Group.RawDescription, x.Group.ItemCode),
							x.TargetCategoryId)).ToArray()), ct));
	});

	[HttpGet("{id:guid}")]
	[EndpointSummary("Read historical classification progress")]
	public Task<Results<Ok<HistoricalProgress>, BadRequestProblem, NotFoundProblem, ConflictProblem>> Read(
			Guid id, CancellationToken ct) => Run(async () =>
			id == Guid.Empty ? ApiProblem.BadRequest("Operation ID is required.") :
			TypedResults.Ok(await service.ReadAsync(Actor(), id, ct)));

	[HttpGet("{id:guid}/summary")]
	[EndpointSummary("Read own captured historical operation choices and progress")]
	public async Task<Results<Ok<HistoricalOperationSummary>, BadRequestProblem, NotFoundProblem>> Summary(
			Guid id, CancellationToken ct)
	{
		if (id == Guid.Empty) { return ApiProblem.BadRequest("Operation ID is required."); }
		try { return TypedResults.Ok(await service.ReadSummaryAsync(Actor(), id, ct)); }
		catch (TaxonomyException error) when (error.Status == 404)
		{ return ApiProblem.NotFound(error.Message); }
	}

	[HttpGet("{id:guid}/items")]
	[EndpointSummary("Read paged frozen item evidence and outcomes")]
	public Task<Results<Ok<HistoricalItemPage>, BadRequestProblem, NotFoundProblem, ConflictProblem>> Items(
			Guid id, [FromQuery] int offset = 0, [FromQuery] int limit = 100, CancellationToken ct = default) => RunItems(async () =>
			id == Guid.Empty || offset < 0 || limit is < 1 or > HistoricalReclassificationPolicy.MaximumChunkSize
					? ApiProblem.BadRequest("Operation ID, nonnegative offset and limit between 1 and 100 are required.")
					: TypedResults.Ok(await service.ReadItemsAsync(Actor(), id, offset, limit, ct)));

	[HttpPost("{id:guid}/apply")]
	[EndpointSummary("Apply the next bounded preview chunk")]
	public Task<Results<Ok<HistoricalProgress>, BadRequestProblem, NotFoundProblem, ConflictProblem>> Apply(
			Guid id, [FromBody] HistoricalChunkBody body, CancellationToken ct) => Run(async () =>
			id == Guid.Empty || body.Limit is < 1 or > HistoricalReclassificationPolicy.MaximumChunkSize
					? ApiProblem.BadRequest("Operation ID and chunk limit between 1 and 100 are required.")
					: TypedResults.Ok(await service.ApplyChunkAsync(Actor(), id, body.Limit, ct)));

	[HttpPost("{id:guid}/cancel")]
	[EndpointSummary("Cancel pending historical classification work")]
	public Task<Results<Ok<HistoricalProgress>, BadRequestProblem, NotFoundProblem, ConflictProblem>> Cancel(
			Guid id, CancellationToken ct) => Run(async () =>
			id == Guid.Empty ? ApiProblem.BadRequest("Operation ID is required.") :
			TypedResults.Ok(await service.CancelAsync(Actor(), id, ct)));

	[HttpPost("{id:guid}/undo")]
	[EndpointSummary("Conditionally undo the next bounded applied chunk")]
	public Task<Results<Ok<HistoricalProgress>, BadRequestProblem, NotFoundProblem, ConflictProblem>> Undo(
			Guid id, [FromBody] HistoricalUndoBody body, CancellationToken ct) => Run(async () =>
			id == Guid.Empty || body.TaxonomyRevisionId == Guid.Empty || string.IsNullOrWhiteSpace(body.Reason) ||
			body.Reason.Contains('\0') || body.Limit is < 1 or > HistoricalReclassificationPolicy.MaximumChunkSize
					? ApiProblem.BadRequest("Operation ID, current taxonomy revision, reason and chunk limit between 1 and 100 are required.")
					: TypedResults.Ok(await service.UndoChunkAsync(Actor(), id,
							new(body.TaxonomyRevisionId, body.Reason), body.Limit, ct)));

	private string Actor() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

	private static async Task<Results<Ok<HistoricalProgress>, BadRequestProblem, NotFoundProblem, ConflictProblem>> Run(
			Func<Task<Results<Ok<HistoricalProgress>, BadRequestProblem, NotFoundProblem, ConflictProblem>>> action)
	{
		try { return await action(); }
		catch (TaxonomyException error)
		{
			return error.Status switch
			{
				404 => ApiProblem.NotFound(error.Message),
				409 => ApiProblem.Conflict(error.Message, error.Extensions),
				_ => ApiProblem.BadRequest(error.Message)
			};
		}
		catch (ArgumentException error) { return ApiProblem.BadRequest(error.Message); }
		catch (InvalidOperationException error) { return ApiProblem.Conflict(error.Message); }
	}

	private static async Task<Results<Ok<HistoricalItemPage>, BadRequestProblem, NotFoundProblem, ConflictProblem>> RunItems(
			Func<Task<Results<Ok<HistoricalItemPage>, BadRequestProblem, NotFoundProblem, ConflictProblem>>> action)
	{
		try { return await action(); }
		catch (TaxonomyException error)
		{
			return error.Status switch
			{
				404 => ApiProblem.NotFound(error.Message),
				409 => ApiProblem.Conflict(error.Message, error.Extensions),
				_ => ApiProblem.BadRequest(error.Message)
			};
		}
		catch (ArgumentException error) { return ApiProblem.BadRequest(error.Message); }
	}
}

public sealed record HistoricalPreviewBody(Guid OperationId, Guid TaxonomyRevisionId, string Reason,
		IReadOnlyList<HistoricalGroupChoiceBody> Groups);
public sealed record HistoricalGroupChoiceBody(HistoricalGroupKey Group, Guid? TargetCategoryId);
public sealed record HistoricalChunkBody(int Limit);
public sealed record HistoricalUndoBody(Guid TaxonomyRevisionId, string Reason, int Limit);
