using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.Category.Create;
using Application.Commands.Category.Delete;
using Application.Commands.Category.Restore;
using Application.Commands.Category.Update;
using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.Category;
using Asp.Versioning;
using Domain.Core;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers.Core;

[ApiVersion("1.0")]
[ApiController]
[Route("api/categories")]
[Produces("application/json")]
[Authorize]
public class CategoriesController(IMediator mediator, CategoryMapper mapper, ILogger<CategoriesController> logger, IEntityChangeNotifier notifier, ICategoryService categoryService) : ControllerBase
{
	public const string RouteGetById = "{id}";
	public const string RouteGetAll = "";
	public const string RouteCreate = "";
	public const string RouteCreateBatch = "batch";
	public const string RouteUpdate = "{id}";
	public const string RouteUpdateBatch = "batch";
	public const string RouteDelete = "{id}";
	public const string RouteGetDeleted = "deleted";
	public const string RouteRestore = "{id}/restore";

	[HttpGet(RouteGetById)]
	[EndpointSummary("Get a category by ID")]
	[EndpointDescription("Returns a single category matching the provided GUID.")]
	public async Task<Results<Ok<CategoryResponse>, NotFound>> GetCategoryById([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		GetCategoryByIdQuery query = new(id);
		Category? result = await mediator.Send(query, cancellationToken);

		if (result == null)
		{
			logger.LogWarning("Category {Id} not found", id);
			return TypedResults.NotFound();
		}

		CategoryResponse model = mapper.ToResponse(result);
		return TypedResults.Ok(model);
	}

	[HttpGet(RouteGetAll)]
	[EndpointSummary("Get all categories")]
	public async Task<Results<Ok<CategoryListResponse>, BadRequest<ProblemDetails>>> GetAllCategories([FromQuery] bool? isActive = null, [FromQuery] string? q = null, [FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null, CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return ApiProblem.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return ApiProblem.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.Category.Contains(sortBy))
		{
			return ApiProblem.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.Category)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return ApiProblem.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		GetAllCategoriesQuery query = new(offset, limit, sort, isActive, q);
		PagedResult<Category> result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new CategoryListResponse
		{
			Data = [.. result.Data.Select(mapper.ToResponse)],
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}

	[HttpGet(RouteGetDeleted)]
	[EndpointSummary("Get all soft-deleted categories")]
	[EndpointDescription("Returns all categories that have been soft-deleted.")]
	public async Task<Results<Ok<CategoryListResponse>, BadRequest<ProblemDetails>>> GetDeletedCategories([FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null, CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return ApiProblem.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return ApiProblem.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.Category.Contains(sortBy))
		{
			return ApiProblem.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.Category)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return ApiProblem.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		GetDeletedCategoriesQuery query = new(offset, limit, sort);
		PagedResult<Category> result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new CategoryListResponse
		{
			Data = [.. result.Data.Select(mapper.ToResponse)],
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}

	[HttpPost(RouteCreate)]
	[EndpointSummary("Create a single category")]
	public async Task<Ok<CategoryResponse>> CreateCategory([FromBody] CreateCategoryRequest model, CancellationToken cancellationToken = default)
	{
		CreateCategoryCommand command = new([mapper.ToDomain(model)]);
		List<Category> categories = await mediator.Send(command, cancellationToken);
		await notifier.NotifyCreated("category", categories[0].Id);
		return TypedResults.Ok(mapper.ToResponse(categories[0]));
	}

	[HttpPost(RouteCreateBatch)]
	[EndpointSummary("Create categories in batch")]
	public async Task<Ok<List<CategoryResponse>>> CreateCategories([FromBody] List<CreateCategoryRequest> models, CancellationToken cancellationToken = default)
	{
		CreateCategoryCommand command = new([.. models.Select(mapper.ToDomain)]);
		List<Category> categories = await mediator.Send(command, cancellationToken);
		await notifier.NotifyBulkChanged("category", "created", categories.Select(c => c.Id));
		return TypedResults.Ok(categories.Select(mapper.ToResponse).ToList());
	}

	[HttpPut(RouteUpdate)]
	[EndpointSummary("Update a single category")]
	public async Task<Results<NoContent, NotFound>> UpdateCategory([FromRoute] Guid id, [FromBody] UpdateCategoryRequest model, CancellationToken cancellationToken = default)
	{
		// Route id is authoritative; ignore any mismatched body id (RECEIPTS-793).
		model.Id = id;
		UpdateCategoryCommand command = new([mapper.ToDomain(model)]);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("Category {Id} not found for update", id);
			return TypedResults.NotFound();
		}

		await notifier.NotifyUpdated("category", id);
		return TypedResults.NoContent();
	}

	[HttpPut(RouteUpdateBatch)]
	[EndpointSummary("Update categories in batch")]
	public async Task<Results<NoContent, NotFound>> UpdateCategories([FromBody] List<UpdateCategoryRequest> models, CancellationToken cancellationToken = default)
	{
		UpdateCategoryCommand command = new([.. models.Select(mapper.ToDomain)]);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("Categories batch update failed — not found");
			return TypedResults.NotFound();
		}

		await notifier.NotifyBulkChanged("category", "updated", models.Select(m => m.Id));
		return TypedResults.NoContent();
	}

	[HttpDelete(RouteDelete)]
	[EndpointSummary("Soft-delete a category")]
	[EndpointDescription("Soft-deletes a category and cascade soft-deletes its subcategories. Returns 409 Conflict if receipt items reference this category or any of its subcategories.")]
	public async Task<Results<NoContent, NotFound, Conflict<ProblemDetails>>> DeleteCategory([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		Category? category = await mediator.Send(new GetCategoryByIdQuery(id), cancellationToken);
		if (category == null)
		{
			logger.LogWarning("Category {Id} not found for deletion", id);
			return TypedResults.NotFound();
		}

		int receiptItemCount = await categoryService.GetReceiptItemCountByCategoryNameAsync(category.Name, cancellationToken);
		if (receiptItemCount > 0)
		{
			logger.LogWarning("Category {Id} cannot be deleted — {Count} receipt items reference its category name", id, receiptItemCount);
			return ApiProblem.Conflict(
				$"Cannot delete — {receiptItemCount} receipt item(s) use this category",
				new Dictionary<string, object?> { ["receiptItemCount"] = receiptItemCount });
		}

		List<string> subcategoryNames = await categoryService.GetSubcategoryNamesAsync(id, cancellationToken);
		int subReceiptItemCount = await categoryService.GetReceiptItemCountBySubcategoryNamesAsync(subcategoryNames, cancellationToken);
		if (subReceiptItemCount > 0)
		{
			logger.LogWarning("Category {Id} cannot be deleted — {Count} receipt items reference its subcategories", id, subReceiptItemCount);
			return ApiProblem.Conflict(
				$"Cannot delete — {subReceiptItemCount} receipt item(s) use subcategories of this category",
				new Dictionary<string, object?> { ["receiptItemCount"] = subReceiptItemCount });
		}

		DeleteCategoryCommand command = new([id]);
		await mediator.Send(command, cancellationToken);

		await notifier.NotifyDeleted("category", id);
		return TypedResults.NoContent();
	}

	[HttpPost(RouteRestore)]
	[EndpointSummary("Restore a soft-deleted category")]
	[EndpointDescription("Restores a previously soft-deleted category and its cascade-deleted subcategories.")]
	public async Task<Results<NoContent, NotFound>> RestoreCategory([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		RestoreCategoryCommand command = new(id);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("Category {Id} not found or not deleted for restore", id);
			return TypedResults.NotFound();
		}

		await notifier.NotifyUpdated("category", id);
		return TypedResults.NoContent();
	}
}
