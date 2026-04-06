using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.ReceiptItem.Create;
using Application.Commands.ReceiptItem.Delete;
using Application.Commands.ReceiptItem.Restore;
using Application.Commands.ReceiptItem.Update;
using Application.Models;
using Application.Queries.Core.ReceiptItem;
using Application.Queries.Core.ReceiptItem.GetReceiptItemSuggestions;
using Application.Queries.Core.Ynab;
using Asp.Versioning;
using Domain.Core;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers.Core;

[ApiVersion("1.0")]
[ApiController]
[Route("api/receipt-items")]
[Produces("application/json")]
[Authorize]
public class ReceiptItemsController(IMediator mediator, ReceiptItemMapper mapper, ILogger<ReceiptItemsController> logger, IEntityChangeNotifier notifier) : ControllerBase
{
	public const string RouteGetById = "{id}";
	public const string RouteGetAll = "";
	public const string RouteCreate = "~/api/receipts/{receiptId}/receipt-items";
	public const string RouteCreateBatch = "~/api/receipts/{receiptId}/receipt-items/batch";
	public const string RouteUpdate = "{id}";
	public const string RouteUpdateBatch = "batch";
	public const string RouteDelete = "";
	public const string RouteGetDeleted = "deleted";
	public const string RouteRestore = "{id}/restore";
	public const string RouteGetSuggestions = "suggestions";
	public const string RouteGetDistinctCategories = "distinct-categories";

	[HttpGet(RouteGetById)]
	[EndpointSummary("Get a receipt item by ID")]
	[EndpointDescription("Returns a single receipt item matching the provided GUID.")]
	public async Task<Results<Ok<ReceiptItemResponse>, NotFound>> GetReceiptItemById([FromRoute] Guid id)
	{
		GetReceiptItemByIdQuery query = new(id);
		ReceiptItem? result = await mediator.Send(query);

		if (result == null)
		{
			logger.LogWarning("ReceiptItem {Id} not found", id);
			return TypedResults.NotFound();
		}

		ReceiptItemResponse model = mapper.ToResponse(result);
		return TypedResults.Ok(model);
	}

	[HttpGet(RouteGetAll)]
	[EndpointSummary("Get all receipt items")]
	public async Task<Results<Ok<ReceiptItemListResponse>, BadRequest<string>>> GetAllReceiptItems([FromQuery] Guid? receiptId = null, [FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null)
	{
		if (offset < 0)
		{
			return TypedResults.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return TypedResults.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.ReceiptItem.Contains(sortBy))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.ReceiptItem)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);

		if (receiptId.HasValue)
		{
			GetReceiptItemsByReceiptIdQuery byReceiptQuery = new(receiptId.Value, offset, limit, sort);
			PagedResult<ReceiptItem> byReceiptResult = await mediator.Send(byReceiptQuery);

			return TypedResults.Ok(new ReceiptItemListResponse
			{
				Data = [.. byReceiptResult.Data.Select(mapper.ToResponse)],
				Total = byReceiptResult.Total,
				Offset = byReceiptResult.Offset,
				Limit = byReceiptResult.Limit,
			});
		}

		GetAllReceiptItemsQuery query = new(offset, limit, sort);
		PagedResult<ReceiptItem> result = await mediator.Send(query);

		return TypedResults.Ok(new ReceiptItemListResponse
		{
			Data = [.. result.Data.Select(mapper.ToResponse)],
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}

	[HttpGet(RouteGetDeleted)]
	[EndpointSummary("Get all soft-deleted receipt items")]
	[EndpointDescription("Returns all receipt items that have been soft-deleted.")]
	public async Task<Results<Ok<ReceiptItemListResponse>, BadRequest<string>>> GetDeletedReceiptItems([FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null)
	{
		if (offset < 0)
		{
			return TypedResults.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return TypedResults.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.ReceiptItem.Contains(sortBy))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.ReceiptItem)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		GetDeletedReceiptItemsQuery query = new(offset, limit, sort);
		PagedResult<ReceiptItem> result = await mediator.Send(query);

		return TypedResults.Ok(new ReceiptItemListResponse
		{
			Data = [.. result.Data.Select(mapper.ToResponse)],
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}

	[HttpPost(RouteCreate)]
	[EndpointSummary("Create a single receipt item")]
	public async Task<Ok<ReceiptItemResponse>> CreateReceiptItem([FromBody] CreateReceiptItemRequest model, [FromRoute] Guid receiptId)
	{
		CreateReceiptItemCommand command = new([mapper.ToDomain(model)], receiptId);
		List<ReceiptItem> receiptItems = await mediator.Send(command);
		await notifier.NotifyCreated("receipt-item", receiptItems[0].Id);
		return TypedResults.Ok(mapper.ToResponse(receiptItems[0]));
	}

	[HttpPost(RouteCreateBatch)]
	[EndpointSummary("Create receipt items in batch")]
	public async Task<Ok<List<ReceiptItemResponse>>> CreateReceiptItems([FromBody] List<CreateReceiptItemRequest> models, [FromRoute] Guid receiptId)
	{
		CreateReceiptItemCommand command = new([.. models.Select(mapper.ToDomain)], receiptId);
		List<ReceiptItem> receiptItems = await mediator.Send(command);
		await notifier.NotifyBulkChanged("receipt-item", "created", receiptItems.Select(ri => ri.Id));
		return TypedResults.Ok(receiptItems.Select(mapper.ToResponse).ToList());
	}

	[HttpPut(RouteUpdate)]
	[EndpointSummary("Update a single receipt item")]
	public async Task<Results<NoContent, NotFound>> UpdateReceiptItem([FromBody] UpdateReceiptItemRequest model, [FromRoute] Guid id)
	{
		UpdateReceiptItemCommand command = new([mapper.ToDomain(model)]);
		bool result = await mediator.Send(command);

		if (!result)
		{
			logger.LogWarning("ReceiptItem {Id} not found for update", id);
			return TypedResults.NotFound();
		}

		await notifier.NotifyUpdated("receipt-item", id);
		return TypedResults.NoContent();
	}

	[HttpPut(RouteUpdateBatch)]
	[EndpointSummary("Update receipt items in batch")]
	public async Task<Results<NoContent, NotFound>> UpdateReceiptItems([FromBody] List<UpdateReceiptItemRequest> models)
	{
		UpdateReceiptItemCommand command = new([.. models.Select(mapper.ToDomain)]);
		bool result = await mediator.Send(command);

		if (!result)
		{
			logger.LogWarning("ReceiptItems batch update failed — not found");
			return TypedResults.NotFound();
		}

		await notifier.NotifyBulkChanged("receipt-item", "updated", models.Select(m => m.Id));
		return TypedResults.NoContent();
	}

	[HttpDelete(RouteDelete)]
	[EndpointSummary("Delete receipt items")]
	[EndpointDescription("Deletes one or more receipt items by their IDs. Returns 404 if any item is not found.")]
	public async Task<Results<NoContent, NotFound>> DeleteReceiptItems([FromBody] List<Guid> ids)
	{
		DeleteReceiptItemCommand command = new(ids);
		bool result = await mediator.Send(command);

		if (!result)
		{
			logger.LogWarning("ReceiptItems delete failed — not found");
			return TypedResults.NotFound();
		}

		await notifier.NotifyBulkChanged("receipt-item", "deleted", ids);
		return TypedResults.NoContent();
	}

	[HttpPost(RouteRestore)]
	[EndpointSummary("Restore a soft-deleted receipt item")]
	[EndpointDescription("Restores a previously soft-deleted receipt item by clearing its DeletedAt timestamp.")]
	public async Task<Results<NoContent, NotFound>> RestoreReceiptItem([FromRoute] Guid id)
	{
		RestoreReceiptItemCommand command = new(id);
		bool result = await mediator.Send(command);

		if (!result)
		{
			logger.LogWarning("ReceiptItem {Id} not found or not deleted for restore", id);
			return TypedResults.NotFound();
		}

		await notifier.NotifyUpdated("receipt-item", id);
		return TypedResults.NoContent();
	}

	[HttpGet(RouteGetSuggestions)]
	[EndpointSummary("Get item code suggestions from receipt history")]
	[EndpointDescription("Returns suggestions for receipt item entry based on historical receipt items, grouped by itemCode and optionally filtered by location.")]
	public async Task<Ok<List<ReceiptItemSuggestionResponse>>> GetSuggestions([FromQuery] string itemCode, [FromQuery] string? location = null, [FromQuery] int limit = 10)
	{
		GetReceiptItemSuggestionsQuery query = new(itemCode, location, limit);
		IEnumerable<ReceiptItemSuggestion> results = await mediator.Send(query);

		List<ReceiptItemSuggestionResponse> response = [.. results.Select(r => new ReceiptItemSuggestionResponse
		{
			ItemCode = r.ItemCode,
			Description = r.Description,
			Category = r.Category,
			Subcategory = r.Subcategory,
			UnitPrice = (double)r.UnitPrice,
			MatchType = r.MatchType == "location" ? ReceiptItemSuggestionResponseMatchType.Location : ReceiptItemSuggestionResponseMatchType.Global,
		})];

		return TypedResults.Ok(response);
	}

	[HttpGet(RouteGetDistinctCategories)]
	[EndpointSummary("Get distinct receipt item categories")]
	[EndpointDescription("Returns the distinct Category values from all non-deleted receipt items.")]
	public async Task<Ok<DistinctCategoriesResponse>> GetDistinctCategories(CancellationToken cancellationToken)
	{
		List<string> categories = await mediator.Send(new GetDistinctReceiptItemCategoriesQuery(), cancellationToken);
		return TypedResults.Ok(new DistinctCategoriesResponse { Categories = categories.ToList() });
	}
}
