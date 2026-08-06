using API.Filters;
using API.Generated.Dtos;
using API.Mapping.Core;
using Application.Commands.Ynab.AccountMapping;
using Application.Commands.Ynab.CategoryMapping;
using Application.Commands.Ynab.MemoSync;
using Application.Commands.Ynab.PushTransactions;
using Application.Commands.Ynab.SelectBudget;
using Application.Commands.Ynab.StaleMappings;
using Application.Exceptions;
using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using Asp.Versioning;
using Common;
using Infrastructure.Ynab;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/ynab")]
[Produces("application/json")]
[Authorize]
public class YnabController(IMediator mediator, IYnabApiClient ynabClient, IYnabBudgetSelectionService budgetSelectionService, YnabMapper mapper, ILogger<YnabController> logger) : ControllerBase
{
	[HttpGet("connection-status")]
	[EndpointSummary("Get YNAB connection status")]
	[EndpointDescription("Returns whether YNAB is configured, whether the connection is active, and the last successful sync timestamp.")]
	public async Task<Ok<YnabConnectionStatusResponse>> GetConnectionStatus(CancellationToken cancellationToken)
	{
		YnabConnectionStatus status = await mediator.Send(new GetYnabConnectionStatusQuery(), cancellationToken);
		return TypedResults.Ok(mapper.ToConnectionStatusResponse(status));
	}

	[HttpGet("status")]
	[EndpointSummary("Get YNAB integration status and sync stats")]
	[EndpointDescription("Returns an aggregate health snapshot: configured flag, last validated/push timestamps, and push counts by window. Derived from the sync-event log; performs no live YNAB call.")]
	public async Task<Ok<YnabStatusResponse>> GetStatus(CancellationToken cancellationToken)
	{
		YnabStatus status = await mediator.Send(new GetYnabStatusQuery(), cancellationToken);
		return TypedResults.Ok(mapper.ToStatusResponse(status));
	}

	[HttpGet("events")]
	[EndpointSummary("Get recent YNAB sync events with optional filtering")]
	[EndpointDescription("Paginated, filterable feed of YNAB push and validate attempts, most recent first.")]
	public async Task<Results<Ok<YnabSyncEventListResponse>, BadRequest<ProblemDetails>>> GetEvents(
		[FromQuery] int offset = 0,
		[FromQuery] int limit = 50,
		[FromQuery] string? sortBy = null,
		[FromQuery] string? sortDirection = null,
		[FromQuery] string? outcome = null,
		[FromQuery] DateTimeOffset? dateFrom = null,
		[FromQuery] DateTimeOffset? dateTo = null,
		CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return ApiProblem.BadRequest("offset must be non-negative");
		}

		if (limit <= 0 || limit > 500)
		{
			return ApiProblem.BadRequest("limit must be between 1 and 500");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return ApiProblem.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		bool? success;
		switch (outcome?.ToLowerInvariant())
		{
			case null or "":
				success = null;
				break;
			case "success":
				success = true;
				break;
			case "failure":
				success = false;
				break;
			default:
				return ApiProblem.BadRequest("outcome must be 'success' or 'failure'");
		}

		SortParams sort = new(sortBy, sortDirection);
		PagedResult<YnabSyncEventDto> result = await mediator.Send(
			new GetYnabSyncEventsQuery(offset, limit, sort, success, dateFrom, dateTo), cancellationToken);
		return TypedResults.Ok(mapper.ToSyncEventListResponse(result));
	}

	[HttpGet("budgets")]
	[EndpointSummary("List YNAB budgets")]
	[EndpointDescription("Returns the list of budgets from the connected YNAB account.")]
	[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
	[RequireYnabConfigured]
	public async Task<Results<Ok<YnabBudgetListResponse>, StatusCodeHttpResult>> GetBudgets(CancellationToken cancellationToken)
	{
		try
		{
			List<YnabBudget> budgets = await mediator.Send(new GetYnabBudgetsQuery(), cancellationToken);
			return TypedResults.Ok(mapper.ToBudgetListResponse(budgets));
		}
		catch (YnabAuthException ex)
		{
			logger.LogWarning(ex, "YNAB authentication failed");
			return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
		}
	}

	[HttpGet("accounts")]
	[EndpointSummary("List YNAB accounts")]
	[EndpointDescription("Returns the list of open/active accounts from the selected YNAB budget.")]
	[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
	[RequireYnabConfigured]
	public async Task<Results<Ok<YnabAccountListResponse>, StatusCodeHttpResult>> GetAccounts(CancellationToken cancellationToken)
	{
		try
		{
			YnabBudgetSelection selection = await mediator.Send(new GetSelectedYnabBudgetQuery(), cancellationToken);
			if (string.IsNullOrEmpty(selection.SelectedBudgetId))
			{
				return TypedResults.Ok(mapper.ToAccountListResponse([]));
			}

			List<YnabAccount> accounts = await ynabClient.GetAccountsAsync(selection.SelectedBudgetId, cancellationToken);

			// Return only open (not closed) accounts
			List<YnabAccount> activeAccounts = accounts.Where(a => !a.Closed).ToList();
			return TypedResults.Ok(mapper.ToAccountListResponse(activeAccounts));
		}
		catch (YnabAuthException ex)
		{
			logger.LogWarning(ex, "YNAB authentication failed");
			return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
		}
	}

	[HttpGet("categories")]
	[EndpointSummary("List YNAB categories")]
	[EndpointDescription("Returns all YNAB categories for the selected budget, grouped by category group, with hidden categories excluded.")]
	[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
	[RequireYnabConfigured]
	public async Task<Results<Ok<YnabCategoryListResponse>, StatusCodeHttpResult>> GetCategories(CancellationToken cancellationToken)
	{
		string? budgetId = await budgetSelectionService.GetSelectedBudgetIdAsync(cancellationToken);
		if (string.IsNullOrEmpty(budgetId))
		{
			logger.LogWarning("No YNAB budget selected");
			return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
		}

		try
		{
			List<YnabCategory> categories = await mediator.Send(new GetYnabCategoriesQuery(budgetId), cancellationToken);
			List<YnabCategory> visible = categories.Where(c => !c.Hidden).ToList();
			return TypedResults.Ok(mapper.ToCategoryListResponse(visible));
		}
		catch (YnabAuthException ex)
		{
			logger.LogWarning(ex, "YNAB authentication failed");
			return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
		}
	}

	[HttpGet("settings/budget")]
	[EndpointSummary("Get the selected YNAB budget")]
	[EndpointDescription("Returns the currently selected YNAB budget ID.")]
	public async Task<Ok<YnabBudgetSettingsResponse>> GetBudgetSettings(CancellationToken cancellationToken)
	{
		YnabBudgetSelection selection = await mediator.Send(new GetSelectedYnabBudgetQuery(), cancellationToken);
		return TypedResults.Ok(mapper.ToBudgetSettingsResponse(selection));
	}

	[HttpPut("settings/budget")]
	[EndpointSummary("Select a YNAB budget")]
	[EndpointDescription("Sets the active YNAB budget for sync operations.")]
	public async Task<NoContent> SelectBudget([FromBody] SelectYnabBudgetRequest request, CancellationToken cancellationToken)
	{
		await mediator.Send(new SelectYnabBudgetCommand(request.BudgetId), cancellationToken);
		return TypedResults.NoContent();
	}

	[HttpGet("account-mappings")]
	[EndpointSummary("List YNAB account mappings")]
	[EndpointDescription("Returns all mappings between receipts accounts and YNAB accounts.")]
	public async Task<Ok<YnabAccountMappingListResponse>> GetAccountMappings(CancellationToken cancellationToken)
	{
		List<YnabAccountMappingDto> mappings = await mediator.Send(new GetYnabAccountMappingsQuery(), cancellationToken);
		return TypedResults.Ok(mapper.ToAccountMappingListResponse(mappings));
	}

	[HttpPost("account-mappings")]
	[EndpointSummary("Create a YNAB account mapping")]
	[EndpointDescription("Maps a receipts account to a YNAB account.")]
	public async Task<Results<Created<YnabAccountMappingResponse>, BadRequest<ProblemDetails>>> CreateAccountMapping(
		[FromBody] CreateYnabAccountMappingRequest request,
		CancellationToken cancellationToken)
	{
		try
		{
			YnabAccountMappingDto mapping = await mediator.Send(
				new CreateYnabAccountMappingCommand(
					request.ReceiptsAccountId,
					request.YnabAccountId,
					request.YnabAccountName,
					request.YnabBudgetId),
				cancellationToken);

			YnabAccountMappingResponse response = mapper.ToAccountMappingResponse(mapping);
			return TypedResults.Created($"/api/ynab/account-mappings/{response.Id}", response);
		}
		catch (ArgumentException ex)
		{
			return ApiProblem.BadRequest(ex.Message);
		}
	}

	[HttpPut("account-mappings/{id}")]
	[EndpointSummary("Update a YNAB account mapping")]
	[EndpointDescription("Updates the YNAB account for an existing mapping.")]
	public async Task<Results<NoContent, NotFound>> UpdateAccountMapping(
		[FromRoute] Guid id,
		[FromBody] UpdateYnabAccountMappingRequest request,
		CancellationToken cancellationToken)
	{
		YnabAccountMappingDto? existing = await mediator.Send(new GetYnabAccountMappingByIdQuery(id), cancellationToken);
		if (existing is null)
		{
			return TypedResults.NotFound();
		}

		await mediator.Send(
			new UpdateYnabAccountMappingCommand(
				id,
				request.YnabAccountId,
				request.YnabAccountName,
				request.YnabBudgetId),
			cancellationToken);

		return TypedResults.NoContent();
	}

	[HttpDelete("account-mappings/{id}")]
	[EndpointSummary("Delete a YNAB account mapping")]
	[EndpointDescription("Removes a mapping between a receipts account and a YNAB account.")]
	public async Task<Results<NoContent, NotFound>> DeleteAccountMapping(
		[FromRoute] Guid id,
		CancellationToken cancellationToken)
	{
		YnabAccountMappingDto? existing = await mediator.Send(new GetYnabAccountMappingByIdQuery(id), cancellationToken);
		if (existing is null)
		{
			return TypedResults.NotFound();
		}

		await mediator.Send(new DeleteYnabAccountMappingCommand(id), cancellationToken);
		return TypedResults.NoContent();
	}

	[HttpGet("category-mappings")]
	[EndpointSummary("List all category mappings")]
	[EndpointDescription("Returns all receipts-to-YNAB category mappings.")]
	public async Task<Ok<YnabCategoryMappingListResponse>> GetCategoryMappings(CancellationToken cancellationToken)
	{
		List<YnabCategoryMappingDto> mappings = await mediator.Send(new GetAllYnabCategoryMappingsQuery(), cancellationToken);
		return TypedResults.Ok(mapper.ToCategoryMappingListResponse(mappings));
	}

	[HttpPost("category-mappings")]
	[EndpointSummary("Create a category mapping")]
	[EndpointDescription("Creates a new mapping from a receipts category to a YNAB category.")]
	public async Task<Results<Created<YnabCategoryMappingResponse>, Conflict<ProblemDetails>>> CreateCategoryMapping(
		[FromBody] CreateYnabCategoryMappingRequest request,
		CancellationToken cancellationToken)
	{
		try
		{
			YnabCategoryMappingDto mapping = await mediator.Send(new CreateYnabCategoryMappingCommand(
				request.ReceiptsCategory,
				request.YnabCategoryId,
				request.YnabCategoryName,
				request.YnabCategoryGroupName,
				request.YnabBudgetId), cancellationToken);

			YnabCategoryMappingResponse response = mapper.ToCategoryMappingResponse(mapping);
			return TypedResults.Created($"/api/ynab/category-mappings/{mapping.Id}", response);
		}
		catch (DuplicateEntityException ex)
		{
			return ApiProblem.Conflict(ex.Message);
		}
	}

	[HttpPut("category-mappings/{id}")]
	[EndpointSummary("Update a category mapping")]
	[EndpointDescription("Updates the YNAB category for an existing mapping.")]
	public async Task<Results<NoContent, NotFound>> UpdateCategoryMapping(
		[FromRoute] Guid id,
		[FromBody] UpdateYnabCategoryMappingRequest request,
		CancellationToken cancellationToken)
	{
		YnabCategoryMappingDto? existing = await mediator.Send(new GetYnabCategoryMappingByIdQuery(id), cancellationToken);
		if (existing is null)
		{
			return TypedResults.NotFound();
		}

		await mediator.Send(new UpdateYnabCategoryMappingCommand(
			id,
			request.YnabCategoryId,
			request.YnabCategoryName,
			request.YnabCategoryGroupName,
			request.YnabBudgetId), cancellationToken);

		return TypedResults.NoContent();
	}

	[HttpDelete("category-mappings/{id}")]
	[EndpointSummary("Delete a category mapping")]
	[EndpointDescription("Permanently deletes a category mapping.")]
	public async Task<Results<NoContent, NotFound>> DeleteCategoryMapping([FromRoute] Guid id, CancellationToken cancellationToken)
	{
		YnabCategoryMappingDto? existing = await mediator.Send(new GetYnabCategoryMappingByIdQuery(id), cancellationToken);
		if (existing is null)
		{
			return TypedResults.NotFound();
		}

		await mediator.Send(new DeleteYnabCategoryMappingCommand(id), cancellationToken);
		return TypedResults.NoContent();
	}

	[HttpGet("category-mappings/unmapped")]
	[EndpointSummary("Get unmapped receipt categories")]
	[EndpointDescription("Returns receipts categories that do not yet have a YNAB category mapping.")]
	public async Task<Ok<UnmappedCategoriesResponse>> GetUnmappedCategories(CancellationToken cancellationToken)
	{
		List<string> unmapped = await mediator.Send(new GetUnmappedCategoriesQuery(), cancellationToken);
		return TypedResults.Ok(new UnmappedCategoriesResponse { UnmappedCategories = unmapped.ToList() });
	}

	[HttpGet("stale-mappings")]
	[EndpointSummary("Check for stale account/category mappings")]
	[EndpointDescription("Returns counts of account and category mappings whose YnabBudgetId does not match the currently selected budget.")]
	public async Task<Ok<StaleMappingsResponse>> GetStaleMappings(CancellationToken cancellationToken)
	{
		StaleMappingsResult result = await mediator.Send(new GetStaleMappingsQuery(), cancellationToken);
		return TypedResults.Ok(mapper.ToStaleMappingsResponse(result));
	}

	[HttpDelete("stale-mappings")]
	[EndpointSummary("Clear stale account/category mappings")]
	[EndpointDescription("Deletes all account and category mappings whose YnabBudgetId does not match the currently selected budget.")]
	public async Task<Ok<ClearStaleMappingsResponse>> ClearStaleMappings(CancellationToken cancellationToken)
	{
		ClearStaleMappingsResult result = await mediator.Send(new ClearStaleMappingsCommand(), cancellationToken);
		return TypedResults.Ok(mapper.ToClearStaleMappingsResponse(result));
	}

	[HttpGet("sync-status/{transactionId}")]
	[EndpointSummary("Get YNAB sync status for a transaction")]
	[EndpointDescription("Returns the sync record for a given local transaction ID and sync type.")]
	public async Task<Results<Ok<YnabSyncRecordResponse>, NotFound>> GetSyncStatus(
		[FromRoute] Guid transactionId,
		[FromQuery] Common.YnabSyncType syncType,
		CancellationToken cancellationToken)
	{
		YnabSyncRecordDto? record = await mediator.Send(new GetYnabSyncRecordByTransactionQuery(transactionId, syncType), cancellationToken);

		if (record is null)
		{
			return TypedResults.NotFound();
		}

		return TypedResults.Ok(mapper.ToSyncRecordResponse(record));
	}

	[HttpPost("sync-memos")]
	[EndpointSummary("Sync YNAB memo for a receipt")]
	[EndpointDescription("Matches local transactions to YNAB transactions and updates their memos with a receipt link.")]
	[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
	[RequireYnabConfigured]
	public async Task<Results<Ok<YnabMemoSyncResponse>, StatusCodeHttpResult>> SyncMemos(
		[FromBody] SyncYnabMemosRequest request,
		CancellationToken cancellationToken)
	{
		try
		{
			List<YnabMemoSyncResult> results = await mediator.Send(new SyncYnabMemosCommand(request.ReceiptId), cancellationToken);
			return TypedResults.Ok(mapper.ToMemoSyncResponse(results));
		}
		catch (YnabAuthException ex)
		{
			logger.LogWarning(ex, "YNAB authentication failed");
			return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
		}
	}

	[HttpPost("push-transactions")]
	[EndpointSummary("Push receipt transactions to YNAB")]
	[EndpointDescription("Creates split transactions in YNAB from a single receipt. Allocates tax and adjustments proportionally across item categories.")]
	[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
	[RequireYnabConfigured]
	public async Task<Results<Ok<PushYnabTransactionsResponse>, BadRequest<PushYnabTransactionsResponse>, StatusCodeHttpResult>> PushTransactions(
		[FromBody] PushYnabTransactionsRequest request,
		CancellationToken cancellationToken)
	{
		try
		{
			PushYnabTransactionsResult result = await mediator.Send(
				new PushYnabTransactionsCommand(request.ReceiptId), cancellationToken);

			PushYnabTransactionsResponse response = mapper.ToPushTransactionsResponse(result);

			if (!result.Success)
			{
				// Not a problem document: a partial push reports per-transaction outcomes the
				// client renders individually, and collapsing that into prose would lose them.
				return TypedResults.BadRequest(response);
			}

			return TypedResults.Ok(response);
		}
		catch (YnabAuthException ex)
		{
			logger.LogWarning(ex, "YNAB authentication failed during push");
			return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
		}
	}

	[HttpPost("sync-memos/bulk")]
	[EndpointSummary("Bulk sync YNAB memos")]
	[EndpointDescription("Batch syncs YNAB memos for multiple receipts.")]
	[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
	[RequireYnabConfigured]
	public async Task<Results<Ok<YnabMemoSyncResponse>, StatusCodeHttpResult>> SyncMemosBulk(
		[FromBody] SyncYnabMemosBulkRequest request,
		CancellationToken cancellationToken)
	{
		try
		{
			List<YnabMemoSyncResult> results = await mediator.Send(
				new SyncYnabMemosBulkCommand(request.ReceiptIds.ToList()), cancellationToken);
			return TypedResults.Ok(mapper.ToMemoSyncResponse(results));
		}
		catch (YnabAuthException ex)
		{
			logger.LogWarning(ex, "YNAB authentication failed");
			return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
		}
	}

	[HttpPost("sync-memos/resolve")]
	[EndpointSummary("Resolve ambiguous YNAB memo sync")]
	[EndpointDescription("Resolves an ambiguous match by linking a local transaction to a specific YNAB transaction.")]
	[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
	[RequireYnabConfigured]
	public async Task<Results<Ok<YnabMemoSyncResultItem>, StatusCodeHttpResult>> ResolveMemoSync(
		[FromBody] ResolveYnabMemoSyncRequest request,
		CancellationToken cancellationToken)
	{
		try
		{
			YnabMemoSyncResult result = await mediator.Send(
				new ResolveYnabMemoSyncCommand(request.LocalTransactionId, request.YnabTransactionId), cancellationToken);
			return TypedResults.Ok(mapper.ToMemoSyncResultItem(result));
		}
		catch (YnabAuthException ex)
		{
			logger.LogWarning(ex, "YNAB authentication failed");
			return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
		}
	}

	[HttpGet("receipts/{receiptId}/split-comparison")]
	[EndpointSummary("Get expected vs actual YNAB category split for a receipt")]
	[EndpointDescription("Returns the expected YNAB category split computed locally alongside the actual state stored in YNAB for any already-pushed transactions.")]
	[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
	[RequireYnabConfigured]
	public async Task<Results<Ok<ReceiptYnabSplitComparisonResponse>, StatusCodeHttpResult>> GetReceiptSplitComparison(
		[FromRoute] Guid receiptId,
		CancellationToken cancellationToken)
	{
		try
		{
			ReceiptYnabSplitComparisonResult result = await mediator.Send(
				new GetReceiptYnabSplitComparisonQuery(receiptId), cancellationToken);
			return TypedResults.Ok(mapper.ToSplitComparisonResponse(result));
		}
		catch (YnabAuthException ex)
		{
			logger.LogWarning(ex, "YNAB authentication failed during split comparison");
			return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
		}
	}

	[HttpGet("rate-limit-status")]
	[EndpointSummary("Get YNAB API rate limit status")]
	[EndpointDescription("Returns the current YNAB API rate limit status including remaining requests, total quota, and window reset time.")]
	public async Task<Ok<YnabRateLimitStatusResponse>> GetRateLimitStatus(CancellationToken cancellationToken)
	{
		YnabRateLimitStatus status = await mediator.Send(new GetYnabRateLimitStatusQuery(), cancellationToken);
		return TypedResults.Ok(mapper.ToRateLimitStatusResponse(status));
	}

	[HttpGet("receipt-sync-statuses")]
	[EndpointSummary("Get YNAB sync statuses for receipts")]
	[EndpointDescription("Returns the aggregate YNAB sync status for each receipt, joining through transactions to sync records.")]
	public async Task<Ok<ReceiptYnabSyncStatusListResponse>> GetReceiptSyncStatuses(
		[FromQuery] List<Guid> receiptIds,
		CancellationToken cancellationToken)
	{
		List<ReceiptYnabSyncStatusDto> statuses = await mediator.Send(
			new GetReceiptYnabSyncStatusesQuery(receiptIds), cancellationToken);
		return TypedResults.Ok(mapper.ToReceiptSyncStatusListResponse(statuses));
	}

	[HttpPost("push-transactions/bulk")]
	[EndpointSummary("Push transactions for multiple receipts to YNAB")]
	[EndpointDescription("Creates split transactions in YNAB for each receipt in the batch. Each receipt is processed independently.")]
	[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
	[RequireYnabConfigured]
	public async Task<Results<Ok<BulkPushYnabTransactionsResponse>, StatusCodeHttpResult>> BulkPushTransactions(
		[FromBody] BulkPushYnabTransactionsRequest request,
		CancellationToken cancellationToken)
	{
		try
		{
			BulkPushYnabTransactionsResult result = await mediator.Send(
				new BulkPushYnabTransactionsCommand(request.ReceiptIds.ToList()), cancellationToken);

			return TypedResults.Ok(mapper.ToBulkPushTransactionsResponse(result));
		}
		catch (YnabAuthException ex)
		{
			logger.LogWarning(ex, "YNAB authentication failed during bulk push");
			return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
		}
	}
}
