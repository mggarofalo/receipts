using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.ItemTemplate.Create;
using Application.Commands.ItemTemplate.Delete;
using Application.Commands.ItemTemplate.Restore;
using Application.Commands.ItemTemplate.Update;
using Application.Models;
using Application.Queries.Core.ItemTemplate;
using Application.Queries.Core.ItemTemplate.GetCategoryRecommendations;
using Application.Queries.Core.ItemTemplate.GetSimilarItems;
using Asp.Versioning;
using Domain.Core;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers.Core;

[ApiVersion("1.0")]
[ApiController]
[Route("api/item-templates")]
[Produces("application/json")]
[Authorize]
public class ItemTemplatesController(IMediator mediator, ItemTemplateMapper mapper, ILogger<ItemTemplatesController> logger, IEntityChangeNotifier notifier) : ControllerBase
{
	public const string RouteGetById = "{id}";
	public const string RouteGetAll = "";
	public const string RouteCreate = "";
	public const string RouteUpdate = "{id}";
	public const string RouteDelete = "";
	public const string RouteGetDeleted = "deleted";
	public const string RouteRestore = "{id}/restore";
	public const string RouteGetSimilar = "similar";
	public const string RouteGetCategorySuggestions = "category-suggestions";

	[HttpGet(RouteGetById)]
	[EndpointSummary("Get an item template by ID")]
	[EndpointDescription("Returns a single item template matching the provided GUID.")]
	public async Task<Results<Ok<ItemTemplateResponse>, NotFound>> GetItemTemplateById([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		GetItemTemplateByIdQuery query = new(id);
		ItemTemplate? result = await mediator.Send(query, cancellationToken);

		if (result == null)
		{
			logger.LogWarning("ItemTemplate {Id} not found", id);
			return TypedResults.NotFound();
		}

		ItemTemplateResponse model = mapper.ToResponse(result);
		return TypedResults.Ok(model);
	}

	[HttpGet(RouteGetAll)]
	[EndpointSummary("Get all item templates")]
	public async Task<Results<Ok<ItemTemplateListResponse>, BadRequest<string>>> GetAllItemTemplates([FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null, CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return TypedResults.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return TypedResults.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.ItemTemplate.Contains(sortBy))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.ItemTemplate)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		GetAllItemTemplatesQuery query = new(offset, limit, sort);
		PagedResult<ItemTemplate> result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new ItemTemplateListResponse
		{
			Data = [.. result.Data.Select(mapper.ToResponse)],
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}

	[HttpGet(RouteGetDeleted)]
	[EndpointSummary("Get all soft-deleted item templates")]
	[EndpointDescription("Returns all item templates that have been soft-deleted.")]
	public async Task<Results<Ok<ItemTemplateListResponse>, BadRequest<string>>> GetDeletedItemTemplates([FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null, CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return TypedResults.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return TypedResults.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.ItemTemplate.Contains(sortBy))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.ItemTemplate)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		GetDeletedItemTemplatesQuery query = new(offset, limit, sort);
		PagedResult<ItemTemplate> result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new ItemTemplateListResponse
		{
			Data = [.. result.Data.Select(mapper.ToResponse)],
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}

	[HttpPost(RouteCreate)]
	[EndpointSummary("Create a single item template")]
	public async Task<Ok<ItemTemplateResponse>> CreateItemTemplate([FromBody] CreateItemTemplateRequest model, CancellationToken cancellationToken = default)
	{
		CreateItemTemplateCommand command = new([mapper.ToDomain(model)]);
		List<ItemTemplate> itemTemplates = await mediator.Send(command, cancellationToken);
		await notifier.NotifyCreated("item-template", itemTemplates[0].Id);
		return TypedResults.Ok(mapper.ToResponse(itemTemplates[0]));
	}

	[HttpPut(RouteUpdate)]
	[EndpointSummary("Update a single item template")]
	public async Task<Results<NoContent, NotFound>> UpdateItemTemplate([FromRoute] Guid id, [FromBody] UpdateItemTemplateRequest model, CancellationToken cancellationToken = default)
	{
		// Route id is authoritative; ignore any mismatched body id (RECEIPTS-793).
		model.Id = id;
		UpdateItemTemplateCommand command = new([mapper.ToDomain(model)]);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("ItemTemplate {Id} not found for update", id);
			return TypedResults.NotFound();
		}

		await notifier.NotifyUpdated("item-template", id);
		return TypedResults.NoContent();
	}

	[HttpDelete(RouteDelete)]
	[EndpointSummary("Delete item templates")]
	[EndpointDescription("Deletes one or more item templates by their IDs. Returns 404 if any item template is not found.")]
	public async Task<Results<NoContent, NotFound>> DeleteItemTemplates([FromBody] List<Guid> ids, CancellationToken cancellationToken = default)
	{
		DeleteItemTemplateCommand command = new(ids);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("ItemTemplates delete failed — not found");
			return TypedResults.NotFound();
		}

		await notifier.NotifyBulkChanged("item-template", "deleted", ids);
		return TypedResults.NoContent();
	}

	[HttpPost(RouteRestore)]
	[EndpointSummary("Restore a soft-deleted item template")]
	[EndpointDescription("Restores a previously soft-deleted item template by clearing its DeletedAt timestamp.")]
	public async Task<Results<NoContent, NotFound>> RestoreItemTemplate([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		RestoreItemTemplateCommand command = new(id);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("ItemTemplate {Id} not found or not deleted for restore", id);
			return TypedResults.NotFound();
		}

		await notifier.NotifyUpdated("item-template", id);
		return TypedResults.NoContent();
	}

	[HttpGet(RouteGetSimilar)]
	[EndpointSummary("Find similar items by hybrid similarity")]
	[EndpointDescription("Returns items from templates and receipt history that are similar to the provided search text, ranked by a weighted combination of trigram and semantic (embedding) similarity.")]
	public async Task<Ok<List<SimilarItemResponse>>> GetSimilarItems([FromQuery] string q, [FromQuery] int limit = 5, [FromQuery] double threshold = 0.3, [FromQuery] bool semantic = true, CancellationToken cancellationToken = default)
	{
		GetSimilarItemsQuery query = new(q, limit, threshold, semantic);
		IEnumerable<SimilarItemResult> results = await mediator.Send(query, cancellationToken);

		List<SimilarItemResponse> response = [.. results.Select(r => new SimilarItemResponse
		{
			Name = r.Name,
			Similarity = r.Similarity,
			SemanticSimilarity = r.SemanticSimilarity,
			CombinedScore = r.CombinedScore,
			Source = r.Source == "template" ? SimilarItemResponseSource.Template : SimilarItemResponseSource.History,
			DefaultCategory = r.DefaultCategory,
			DefaultSubcategory = r.DefaultSubcategory,
			DefaultUnitPrice = r.DefaultUnitPrice.HasValue ? (double)r.DefaultUnitPrice.Value : null,
			DefaultItemCode = r.DefaultItemCode,
		})];

		return TypedResults.Ok(response);
	}

	[HttpGet(RouteGetCategorySuggestions)]
	[EndpointSummary("Get category/subcategory recommendations")]
	[EndpointDescription("Returns ranked category and subcategory suggestions based on semantic and trigram similarity to existing items and receipt history.")]
	public async Task<Ok<List<CategoryRecommendationResponse>>> GetCategorySuggestions([FromQuery] string q, [FromQuery] int limit = 5, CancellationToken cancellationToken = default)
	{
		GetCategoryRecommendationsQuery query = new(q, limit);
		IEnumerable<CategoryRecommendation> results = await mediator.Send(query, cancellationToken);

		List<CategoryRecommendationResponse> response = [.. results.Select(r => new CategoryRecommendationResponse
		{
			Category = r.Category,
			Subcategory = r.Subcategory,
			Confidence = r.Confidence,
			OccurrenceCount = r.OccurrenceCount,
		})];

		return TypedResults.Ok(response);
	}
}
