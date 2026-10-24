using System.Security.Claims;
using API.Generated.Dtos;
using API.Http;
using API.Mapping.Core;
using API.Services;
using Application.Commands.ReceiptItem.Create;
using Application.Commands.ReceiptItem.Delete;
using Application.Commands.ReceiptItem.Restore;
using Application.Commands.ReceiptItem.Update;
using Application.Exceptions;
using Application.Models;
using Application.Queries.Core.ReceiptItem;
using Application.Queries.Core.ReceiptItem.GetReceiptItemSuggestions;
using Application.Queries.Core.Ynab;
using Asp.Versioning;
using Domain.Core;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers.Core;

[ApiVersion("1.0")]
[ApiController]
[Route("api/receipt-items")]
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

	[HttpGet("normalization-candidates")]
	[EndpointSummary("Get bounded advisory product candidates for receipt entry")]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
	public async Task<Results<Ok<List<ReceiptNormalizationSuggestionResponse>>, BadRequestProblem, ForbiddenProblem>> GetReceiptNormalizationCandidates(
		[FromServices] Application.Normalization.IReceiptNormalizationCandidates candidates,
		string location, string description, string? itemCode = null, CancellationToken cancellationToken = default)
	{
		var result = await candidates.GetCandidatesAsync(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
			new(location, description, itemCode), cancellationToken);
		return TypedResults.Ok(result.Select(candidate => new ReceiptNormalizationSuggestionResponse
		{
			Id = candidate.Id,
			Revision = candidate.Revision,
			CanonicalName = candidate.CanonicalName,
			DisplayLabel = candidate.DisplayLabel,
			Channels = candidate.Channels.ToList(),
		}).ToList());
	}

	[HttpPost("normalization-preview")]
	[EndpointSummary("Preview a source decision and search active products without saving")]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
	public async Task<Results<Ok<ReceiptNormalizationPreviewResponse>, BadRequestProblem>> PreviewReceiptNormalization(
		[FromBody] ReceiptNormalizationPreviewRequest request,
		[FromServices] Application.Normalization.IReceiptNormalizationPreview preview,
		CancellationToken cancellationToken = default)
	{
		var result = await preview.PreviewAsync(new(request.Source.Location, request.Source.Description, request.Source.ItemCode), request.Query,
			request.Offset, request.Limit, cancellationToken);
		return TypedResults.Ok(new ReceiptNormalizationPreviewResponse
		{
			SourceFingerprint = result.SourceFingerprint,
			MappingId = result.MappingId,
			MappingRevision = result.MappingRevision,
			TargetId = result.TargetId,
			TargetRevision = result.TargetRevision,
			TargetName = result.TargetName,
			Excluded = result.Excluded,
			SourceAmbiguous = result.SourceAmbiguous,
			Candidates = result.Candidates.Select(value => new API.Generated.Dtos.ReceiptNormalizationCandidate { Id = value.Id, Revision = value.Revision, Name = value.Name }).ToList(),
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}

	[HttpGet(RouteGetById)]
	[EndpointSummary("Get a receipt item by ID")]
	[EndpointDescription("Returns a single receipt item matching the provided GUID.")]
	public async Task<Results<Ok<ReceiptItemResponse>, NotFound>> GetReceiptItemById([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		GetReceiptItemByIdQuery query = new(id);
		ReceiptItem? result = await mediator.Send(query, cancellationToken);

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
	public async Task<Results<Ok<ReceiptItemListResponse>, BadRequestProblem>> GetAllReceiptItems([FromQuery] Guid? receiptId = null, [FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null, [FromQuery] string? q = null, [FromQuery] Guid? normalizedDescriptionId = null, CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return ApiProblem.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return ApiProblem.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.ReceiptItem.Contains(sortBy))
		{
			return ApiProblem.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.ReceiptItem)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return ApiProblem.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);

		if (receiptId.HasValue)
		{
			GetReceiptItemsByReceiptIdQuery byReceiptQuery = new(receiptId.Value, offset, limit, sort);
			PagedResult<ReceiptItem> byReceiptResult = await mediator.Send(byReceiptQuery, cancellationToken);

			return TypedResults.Ok(new ReceiptItemListResponse
			{
				Data = [.. byReceiptResult.Data.Select(mapper.ToResponse)],
				Total = byReceiptResult.Total,
				Offset = byReceiptResult.Offset,
				Limit = byReceiptResult.Limit,
			});
		}

		if (normalizedDescriptionId == Guid.Empty)
		{
			// An empty GUID is a caller mistake, not "no filter". Silently treating it as unfiltered
			// would hand back the whole table to a split dialog asking for one row's items.
			return ApiProblem.BadRequest("normalizedDescriptionId must not be an empty GUID");
		}

		string? normalizedQ = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
		GetAllReceiptItemsQuery query = new(offset, limit, sort, normalizedQ, normalizedDescriptionId);
		PagedResult<ReceiptItem> result = await mediator.Send(query, cancellationToken);

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
	public async Task<Results<Ok<ReceiptItemListResponse>, BadRequestProblem>> GetDeletedReceiptItems([FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null, CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return ApiProblem.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return ApiProblem.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.ReceiptItem.Contains(sortBy))
		{
			return ApiProblem.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.ReceiptItem)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return ApiProblem.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		GetDeletedReceiptItemsQuery query = new(offset, limit, sort);
		PagedResult<ReceiptItem> result = await mediator.Send(query, cancellationToken);

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
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
	public async Task<Results<Ok<ReceiptItemResponse>, BadRequestProblem, ForbiddenProblem, ConflictProblem>> CreateReceiptItem([FromBody] CreateReceiptItemRequest model, [FromRoute] Guid receiptId, CancellationToken cancellationToken = default, [FromQuery] Guid? clientRequestId = null)
	{
		HttpContext?.Response.OnStarting(() =>
		{
			if (Response.StatusCode is 400 or 403 or 409) { Response.ContentType = "application/problem+json"; }
			return Task.CompletedTask;
		});
		CreateReceiptItemCommand command = new([mapper.ToDomain(model)], receiptId, [model.ItemTemplateId], clientRequestId);
		List<ReceiptItem> receiptItems;
		try
		{
			receiptItems = await mediator.Send(command, cancellationToken);
		}
		catch (ConcurrencyConflictException ex)
		{
			return ApiProblem.Conflict(ex.Message);
		}
		await notifier.NotifyCreated("receipt-item", receiptItems[0].Id);
		return TypedResults.Ok(mapper.ToResponse(receiptItems[0]));
	}

	[HttpPost(RouteCreateBatch)]
	[EndpointSummary("Create receipt items in batch")]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
	public async Task<Results<Ok<List<ReceiptItemResponse>>, BadRequestProblem, ForbiddenProblem, ConflictProblem>> CreateReceiptItems([FromBody] List<CreateReceiptItemRequest> models, [FromRoute] Guid receiptId, CancellationToken cancellationToken = default, [FromQuery] Guid? clientRequestId = null)
	{
		HttpContext?.Response.OnStarting(() =>
		{
			if (Response.StatusCode is 400 or 403 or 409) { Response.ContentType = "application/problem+json"; }
			return Task.CompletedTask;
		});
		CreateReceiptItemCommand command = new(
			[.. models.Select(mapper.ToDomain)],
			receiptId,
			[.. models.Select(m => m.ItemTemplateId)], clientRequestId);
		List<ReceiptItem> receiptItems;
		try
		{
			receiptItems = await mediator.Send(command, cancellationToken);
		}
		catch (ConcurrencyConflictException ex)
		{
			return ApiProblem.Conflict(ex.Message);
		}
		await notifier.NotifyBulkChanged("receipt-item", "created", receiptItems.Select(ri => ri.Id));
		return TypedResults.Ok(receiptItems.Select(mapper.ToResponse).ToList());
	}

	[HttpPut(RouteUpdate)]
	[EndpointSummary("Update a single receipt item")]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
	public async Task<Results<NoContent, NotFound, BadRequestProblem, ForbiddenProblem, ConflictProblem>> UpdateReceiptItem([FromBody] UpdateReceiptItemRequest model, [FromRoute] Guid id, CancellationToken cancellationToken = default, [FromQuery] Guid? clientRequestId = null)
	{
		HttpContext?.Response.OnStarting(() =>
		{
			if (Response.StatusCode is 400 or 403 or 409) { Response.ContentType = "application/problem+json"; }
			return Task.CompletedTask;
		});
		// Route id is authoritative; ignore any mismatched body id (RECEIPTS-793).
		model.Id = id;
		UpdateReceiptItemCommand command = new([mapper.ToDomain(model)], clientRequestId);
		bool result = await mediator.Send(command, cancellationToken);

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
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
	public async Task<Results<NoContent, NotFound, BadRequestProblem, ForbiddenProblem, ConflictProblem>> UpdateReceiptItems([FromBody] List<UpdateReceiptItemRequest> models, CancellationToken cancellationToken = default, [FromQuery] Guid? clientRequestId = null)
	{
		HttpContext?.Response.OnStarting(() =>
		{
			if (Response.StatusCode is 400 or 403 or 409) { Response.ContentType = "application/problem+json"; }
			return Task.CompletedTask;
		});
		UpdateReceiptItemCommand command = new([.. models.Select(mapper.ToDomain)], clientRequestId);
		bool result = await mediator.Send(command, cancellationToken);

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
	public async Task<Results<NoContent, NotFound>> DeleteReceiptItems([FromBody] List<Guid> ids, CancellationToken cancellationToken = default)
	{
		DeleteReceiptItemCommand command = new(ids);
		bool result = await mediator.Send(command, cancellationToken);

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
	public async Task<Results<NoContent, NotFound, ConflictProblem>> RestoreReceiptItem([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		RestoreReceiptItemCommand command = new(id);
		bool result;
		try
		{
			result = await mediator.Send(command, cancellationToken);
		}
		catch (ConcurrencyConflictException ex)
		{
			return ApiProblem.Conflict(ex.Message);
		}

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
	public async Task<Ok<List<ReceiptItemSuggestionResponse>>> GetSuggestions([FromQuery] string itemCode, [FromQuery] string? location = null, [FromQuery] int limit = 10, CancellationToken cancellationToken = default)
	{
		GetReceiptItemSuggestionsQuery query = new(itemCode, location, limit);
		IEnumerable<ReceiptItemSuggestion> results = await mediator.Send(query, cancellationToken);

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
