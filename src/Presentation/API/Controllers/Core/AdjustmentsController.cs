using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.Adjustment.Create;
using Application.Commands.Adjustment.Delete;
using Application.Commands.Adjustment.Restore;
using Application.Commands.Adjustment.Update;
using Application.Models;
using Application.Queries.Core.Adjustment;
using Asp.Versioning;
using Domain.Core;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers.Core;

[ApiVersion("1.0")]
[ApiController]
[Route("api/adjustments")]
[Produces("application/json")]
[Authorize]
public class AdjustmentsController(IMediator mediator, AdjustmentMapper mapper, ILogger<AdjustmentsController> logger, IEntityChangeNotifier notifier) : ControllerBase
{
	public const string RouteGetById = "{id}";
	public const string RouteGetAll = "";
	public const string RouteCreate = "~/api/receipts/{receiptId}/adjustments";
	public const string RouteUpdate = "{id}";
	public const string RouteDelete = "";
	public const string RouteGetDeleted = "deleted";
	public const string RouteRestore = "{id}/restore";

	[HttpGet(RouteGetById)]
	[EndpointSummary("Get an adjustment by ID")]
	[EndpointDescription("Returns a single adjustment matching the provided GUID.")]
	public async Task<Results<Ok<AdjustmentResponse>, NotFound>> GetAdjustmentById([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		GetAdjustmentByIdQuery query = new(id);
		Adjustment? result = await mediator.Send(query, cancellationToken);

		if (result == null)
		{
			logger.LogWarning("Adjustment {Id} not found", id);
			return TypedResults.NotFound();
		}

		AdjustmentResponse model = mapper.ToResponse(result);
		return TypedResults.Ok(model);
	}

	[HttpGet(RouteGetAll)]
	[EndpointSummary("Get all adjustments")]
	public async Task<Results<Ok<AdjustmentListResponse>, BadRequest<string>>> GetAllAdjustments([FromQuery] Guid? receiptId = null, [FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null, CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return TypedResults.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return TypedResults.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.Adjustment.Contains(sortBy))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.Adjustment)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);

		if (receiptId.HasValue)
		{
			GetAdjustmentsByReceiptIdQuery byReceiptQuery = new(receiptId.Value, offset, limit, sort);
			PagedResult<Adjustment> byReceiptResult = await mediator.Send(byReceiptQuery, cancellationToken);

			return TypedResults.Ok(new AdjustmentListResponse
			{
				Data = [.. byReceiptResult.Data.Select(mapper.ToResponse)],
				Total = byReceiptResult.Total,
				Offset = byReceiptResult.Offset,
				Limit = byReceiptResult.Limit,
			});
		}

		GetAllAdjustmentsQuery query = new(offset, limit, sort);
		PagedResult<Adjustment> result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new AdjustmentListResponse
		{
			Data = [.. result.Data.Select(mapper.ToResponse)],
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}

	[HttpGet(RouteGetDeleted)]
	[EndpointSummary("Get all soft-deleted adjustments")]
	[EndpointDescription("Returns all adjustments that have been soft-deleted.")]
	public async Task<Results<Ok<AdjustmentListResponse>, BadRequest<string>>> GetDeletedAdjustments([FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null, CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return TypedResults.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return TypedResults.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.Adjustment.Contains(sortBy))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.Adjustment)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		GetDeletedAdjustmentsQuery query = new(offset, limit, sort);
		PagedResult<Adjustment> result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new AdjustmentListResponse
		{
			Data = [.. result.Data.Select(mapper.ToResponse)],
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}

	[HttpPost(RouteCreate)]
	[EndpointSummary("Create a single adjustment")]
	public async Task<Ok<AdjustmentResponse>> CreateAdjustment([FromBody] CreateAdjustmentRequest model, [FromRoute] Guid receiptId, CancellationToken cancellationToken = default)
	{
		CreateAdjustmentCommand command = new([mapper.ToDomain(model)], receiptId);
		List<Adjustment> adjustments = await mediator.Send(command, cancellationToken);
		await notifier.NotifyCreated("adjustment", adjustments[0].Id);
		return TypedResults.Ok(mapper.ToResponse(adjustments[0]));
	}

	[HttpPut(RouteUpdate)]
	[EndpointSummary("Update a single adjustment")]
	public async Task<Results<NoContent, NotFound>> UpdateAdjustment([FromBody] UpdateAdjustmentRequest model, [FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		// Route id is authoritative; ignore any mismatched body id so a PUT can never
		// silently update a resource other than the one the URL names (RECEIPTS-793).
		model.Id = id;
		UpdateAdjustmentCommand command = new([mapper.ToDomain(model)]);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("Adjustment {Id} not found for update", id);
			return TypedResults.NotFound();
		}

		await notifier.NotifyUpdated("adjustment", id);
		return TypedResults.NoContent();
	}

	[HttpDelete(RouteDelete)]
	[EndpointSummary("Delete adjustments")]
	[EndpointDescription("Deletes one or more adjustments by their IDs. Returns 404 if any adjustment is not found.")]
	public async Task<Results<NoContent, NotFound>> DeleteAdjustments([FromBody] List<Guid> ids, CancellationToken cancellationToken = default)
	{
		DeleteAdjustmentCommand command = new(ids);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("Adjustments delete failed — not found");
			return TypedResults.NotFound();
		}

		await notifier.NotifyBulkChanged("adjustment", "deleted", ids);
		return TypedResults.NoContent();
	}

	[HttpPost(RouteRestore)]
	[EndpointSummary("Restore a soft-deleted adjustment")]
	[EndpointDescription("Restores a previously soft-deleted adjustment by clearing its DeletedAt timestamp.")]
	public async Task<Results<NoContent, NotFound>> RestoreAdjustment([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		RestoreAdjustmentCommand command = new(id);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("Adjustment {Id} not found or not deleted for restore", id);
			return TypedResults.NotFound();
		}

		await notifier.NotifyUpdated("adjustment", id);
		return TypedResults.NoContent();
	}
}
