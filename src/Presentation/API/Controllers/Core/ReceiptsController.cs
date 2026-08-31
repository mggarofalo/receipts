using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.Receipt.Create;
using Application.Commands.Receipt.CreateComplete;
using Application.Commands.Receipt.Delete;
using Application.Commands.Receipt.Restore;
using Application.Commands.Receipt.Update;
using Application.Models;
using Application.Queries.Core.Receipt;
using Asp.Versioning;
using Domain.Core;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers.Core;

[ApiVersion("1.0")]
[ApiController]
[Route("api/receipts")]
[Produces("application/json")]
[Authorize]
public class ReceiptsController(
	IMediator mediator,
	ReceiptMapper mapper,
	TransactionMapper transactionMapper,
	ReceiptItemMapper receiptItemMapper,
	AdjustmentMapper adjustmentMapper,
	ILogger<ReceiptsController> logger,
	IEntityChangeNotifier notifier) : ControllerBase
{
	public const string RouteGetById = "{id}";
	public const string RouteGetAll = "";
	public const string RouteCreate = "";
	public const string RouteCreateComplete = "complete";
	public const string RouteCreateBatch = "batch";
	public const string RouteUpdate = "{id}";
	public const string RouteUpdateBatch = "batch";
	public const string RouteDelete = "";
	public const string RouteGetLocations = "locations";
	public const string RouteGetDeleted = "deleted";
	public const string RouteRestore = "{id}/restore";

	[HttpGet(RouteGetById)]
	[EndpointSummary("Get a receipt by ID")]
	[EndpointDescription("Returns a single receipt matching the provided GUID.")]
	public async Task<Results<Ok<ReceiptResponse>, NotFound>> GetReceiptById([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		GetReceiptByIdQuery query = new(id);
		Receipt? result = await mediator.Send(query, cancellationToken);

		if (result == null)
		{
			logger.LogWarning("Receipt {Id} not found", id);
			return TypedResults.NotFound();
		}

		ReceiptResponse model = mapper.ToResponse(result);
		return TypedResults.Ok(model);
	}

	[HttpGet(RouteGetAll)]
	[EndpointSummary("Get all receipts")]
	public async Task<Results<Ok<ReceiptListResponse>, BadRequest<ProblemDetails>>> GetAllReceipts([FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null, [FromQuery] Guid? accountId = null, [FromQuery] Guid? cardId = null, [FromQuery] string? q = null, [FromQuery] string? location = null, CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return ApiProblem.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return ApiProblem.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.Receipt.Contains(sortBy))
		{
			return ApiProblem.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.Receipt)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return ApiProblem.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		string? normalizedQ = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
		// Deliberately NOT trimmed, unlike q: `location` is an exact-match drill-down key that has to
		// reproduce the Spending by Location report's raw GROUP BY value, and a receipt whose Location
		// carries leading/trailing whitespace is its own bucket in that report (nothing in the write
		// path trims Location). Trimming here would make those rows undrillable. See
		// ReceiptRepository.ApplyLocationFilter.
		string? normalizedLocation = string.IsNullOrEmpty(location) ? null : location;
		SortParams sort = new(sortBy, sortDirection);
		GetAllReceiptsQuery query = new(offset, limit, sort, accountId, cardId, normalizedQ, normalizedLocation);
		PagedResult<ReceiptListItem> result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new ReceiptListResponse
		{
			Data = [.. result.Data.Select(r => new ReceiptListItemResponse
			{
				Id = r.Id,
				Location = r.Location,
				Date = r.Date,
				TaxAmount = (double)r.TaxAmount,
				ItemSubtotal = (double)r.ItemSubtotal,
				AdjustmentTotal = (double)r.AdjustmentTotal,
				ExpectedTotal = (double)r.ExpectedTotal,
				TransactionTotal = (double)r.TransactionTotal,
				BalanceState = Enum.Parse<ReceiptListItemResponseBalanceState>(r.BalanceState.Replace("-", ""), true),
				ItemCount = r.ItemCount,
				CategorySummary = r.CategorySummary,
				PaymentSummary = r.PaymentSummary,
			})],
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}

	[HttpGet(RouteGetLocations)]
	[EndpointSummary("Get distinct receipt locations sorted by frequency")]
	[EndpointDescription("Returns distinct location strings from existing receipts, ordered by usage frequency (most-used first). Supports optional prefix filtering.")]
	public async Task<Results<Ok<LocationSuggestionsResponse>, BadRequest<ProblemDetails>>> GetLocations([FromQuery] string? q = null, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
	{
		if (limit <= 0 || limit > 100)
		{
			return ApiProblem.BadRequest("limit must be between 1 and 100");
		}

		GetDistinctLocationsQuery query = new(q, limit);
		List<string> locations = await mediator.Send(query, cancellationToken);

		LocationSuggestionsResponse response = new()
		{
			Locations = [.. locations],
		};

		return TypedResults.Ok(response);
	}

	[HttpGet(RouteGetDeleted)]
	[EndpointSummary("Get all soft-deleted receipts")]
	[EndpointDescription("Returns all receipts that have been soft-deleted.")]
	public async Task<Results<Ok<ReceiptListResponse>, BadRequest<ProblemDetails>>> GetDeletedReceipts([FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null, CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return ApiProblem.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return ApiProblem.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.Receipt.Contains(sortBy))
		{
			return ApiProblem.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.Receipt)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return ApiProblem.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		GetDeletedReceiptsQuery query = new(offset, limit, sort);
		PagedResult<Receipt> result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new ReceiptListResponse
		{
			Data = [.. result.Data.Select(r => new ReceiptListItemResponse
			{
				Id = r.Id,
				Location = r.Location,
				Date = r.Date,
				TaxAmount = (double)r.TaxAmount.Amount,
				ItemSubtotal = 0,
				AdjustmentTotal = 0,
				ExpectedTotal = (double)r.TaxAmount.Amount,
				TransactionTotal = 0,
				BalanceState = ReceiptListItemResponseBalanceState.NoTransactions,
				ItemCount = 0,
				CategorySummary = string.Empty,
				PaymentSummary = string.Empty,
			})],
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}

	[HttpPost(RouteCreate)]
	[EndpointSummary("Create a single receipt")]
	public async Task<Ok<ReceiptResponse>> CreateReceipt([FromBody] CreateReceiptRequest model, CancellationToken cancellationToken = default)
	{
		CreateReceiptCommand command = new([mapper.ToDomain(model)]);
		List<Receipt> receipts = await mediator.Send(command, cancellationToken);
		ReceiptResponse response = mapper.ToResponse(receipts[0]);
		await notifier.NotifyCreated("receipt", receipts[0].Id);
		return TypedResults.Ok(response);
	}

	[HttpPost(RouteCreateComplete)]
	[EndpointSummary("Create a receipt with transactions, items, and adjustments atomically")]
	[EndpointDescription("Creates a receipt along with its transactions, items, and signed receipt-level adjustments in a single atomic operation. If any part fails, nothing is persisted.")]
	public async Task<Results<Ok<CompleteReceiptResponse>, BadRequest<ProblemDetails>>> CreateCompleteReceipt([FromBody] CreateCompleteReceiptRequest model, CancellationToken cancellationToken = default)
	{
		Receipt receipt = mapper.ToDomain(model.Receipt);

		List<Transaction> transactions = [.. model.Transactions.Select(m =>
		{
			Transaction t = transactionMapper.ToDomain(m);
			t.AccountId = m.AccountId;
			return t;
		})];

		List<ReceiptItem> items = [.. model.Items.Select(receiptItemMapper.ToDomain)];
		List<Adjustment> adjustments = [.. (model.Adjustments ?? []).Select(adjustmentMapper.ToDomain)];

		CreateCompleteReceiptCommand command = new(receipt, transactions, items, adjustments);
		CreateCompleteReceiptResult result = await mediator.Send(command, cancellationToken);

		CompleteReceiptResponse response = new()
		{
			Receipt = mapper.ToResponse(result.Receipt),
			Transactions = [.. result.Transactions.Select(transactionMapper.ToResponse)],
			Items = [.. result.Items.Select(receiptItemMapper.ToResponse)],
			Adjustments = [.. result.Adjustments.Select(adjustmentMapper.ToResponse)],
		};

		await notifier.NotifyCreated("receipt", result.Receipt.Id);
		return TypedResults.Ok(response);
	}

	[HttpPost(RouteCreateBatch)]
	[EndpointSummary("Create receipts in batch")]
	public async Task<Ok<List<ReceiptResponse>>> CreateReceipts([FromBody] List<CreateReceiptRequest> models, CancellationToken cancellationToken = default)
	{
		CreateReceiptCommand command = new([.. models.Select(mapper.ToDomain)]);
		List<Receipt> receipts = await mediator.Send(command, cancellationToken);
		List<ReceiptResponse> responses = receipts.Select(mapper.ToResponse).ToList();
		await notifier.NotifyBulkChanged("receipt", "created", receipts.Select(r => r.Id));
		return TypedResults.Ok(responses);
	}

	[HttpPut(RouteUpdate)]
	[EndpointSummary("Update a single receipt")]
	public async Task<Results<NoContent, NotFound>> UpdateReceipt([FromRoute] Guid id, [FromBody] UpdateReceiptRequest model, CancellationToken cancellationToken = default)
	{
		// Route id is authoritative; ignore any mismatched body id (RECEIPTS-793).
		model.Id = id;
		UpdateReceiptCommand command = new([mapper.ToDomain(model)]);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("Receipt {Id} not found for update", id);
			return TypedResults.NotFound();
		}

		await notifier.NotifyUpdated("receipt", id);
		return TypedResults.NoContent();
	}

	[HttpPut(RouteUpdateBatch)]
	[EndpointSummary("Update receipts in batch")]
	public async Task<Results<NoContent, NotFound>> UpdateReceipts([FromBody] List<UpdateReceiptRequest> models, CancellationToken cancellationToken = default)
	{
		UpdateReceiptCommand command = new([.. models.Select(mapper.ToDomain)]);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("Receipts batch update failed — not found");
			return TypedResults.NotFound();
		}

		await notifier.NotifyBulkChanged("receipt", "updated", models.Select(m => m.Id));
		return TypedResults.NoContent();
	}

	[HttpDelete(RouteDelete)]
	[EndpointSummary("Delete receipts")]
	[EndpointDescription("Deletes one or more receipts by their IDs. Returns 404 if any receipt is not found.")]
	public async Task<Results<NoContent, NotFound>> DeleteReceipts([FromBody] List<Guid> ids, CancellationToken cancellationToken = default)
	{
		DeleteReceiptCommand command = new(ids);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("Receipts delete failed — not found");
			return TypedResults.NotFound();
		}

		await notifier.NotifyBulkChanged("receipt", "deleted", ids);
		return TypedResults.NoContent();
	}

	[HttpPost(RouteRestore)]
	[EndpointSummary("Restore a soft-deleted receipt")]
	[EndpointDescription("Restores a previously soft-deleted receipt and its associated items and transactions.")]
	public async Task<Results<NoContent, NotFound>> RestoreReceipt([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		RestoreReceiptCommand command = new(id);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("Receipt {Id} not found or not deleted for restore", id);
			return TypedResults.NotFound();
		}

		await notifier.NotifyUpdated("receipt", id);
		return TypedResults.NoContent();
	}
}
