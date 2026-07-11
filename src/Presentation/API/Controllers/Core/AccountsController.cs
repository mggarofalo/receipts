using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.Account.Create;
using Application.Commands.Account.Delete;
using Application.Commands.Account.Update;
using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.Account;
using Application.Queries.Core.Card;
using Asp.Versioning;
using Domain.Core;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers.Core;

[ApiVersion("1.0")]
[ApiController]
[Route("api/accounts")]
[Produces("application/json")]
[Authorize]
public class AccountsController(IMediator mediator, AccountMapper mapper, CardMapper cardMapper, ILogger<AccountsController> logger, IEntityChangeNotifier notifier, IAccountService accountService) : ControllerBase
{
	public const string RouteGetById = "{id}";
	public const string RouteGetAll = "";
	public const string RouteGetCards = "{id}/cards";
	public const string RouteCreate = "";
	public const string RouteCreateBatch = "batch";
	public const string RouteUpdate = "{id}";
	public const string RouteUpdateBatch = "batch";
	public const string RouteDelete = "{id}";

	[HttpGet(RouteGetById)]
	[EndpointSummary("Get an account by ID")]
	[EndpointDescription("Returns a single account matching the provided GUID.")]
	public async Task<Results<Ok<AccountResponse>, NotFound>> GetAccountById([FromRoute] Guid id)
	{
		GetAccountByIdQuery query = new(id);
		Account? result = await mediator.Send(query, HttpContext.RequestAborted);

		if (result == null)
		{
			logger.LogWarning("Account {Id} not found", id);
			return TypedResults.NotFound();
		}

		AccountResponse model = mapper.ToResponse(result);
		return TypedResults.Ok(model);
	}

	[HttpGet(RouteGetAll)]
	[EndpointSummary("Get all accounts")]
	public async Task<Results<Ok<AccountListResponse>, BadRequest<string>>> GetAllAccounts([FromQuery] bool? isActive = null, [FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null)
	{
		if (offset < 0)
		{
			return TypedResults.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return TypedResults.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.Account.Contains(sortBy))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.Account)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		GetAllAccountsQuery query = new(offset, limit, sort, isActive);
		PagedResult<Account> result = await mediator.Send(query, HttpContext.RequestAborted);

		return TypedResults.Ok(new AccountListResponse
		{
			Data = [.. result.Data.Select(mapper.ToResponse)],
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}

	[HttpGet(RouteGetCards)]
	[EndpointSummary("Get cards for an account")]
	[EndpointDescription("Returns the physical Cards (aliases) that belong to the given logical Account. Returns 404 if the account does not exist.")]
	public async Task<Results<Ok<List<CardResponse>>, NotFound>> GetCardsForAccount([FromRoute] Guid id)
	{
		if (!await accountService.ExistsAsync(id, HttpContext.RequestAborted))
		{
			logger.LogWarning("Account {Id} not found when fetching cards", id);
			return TypedResults.NotFound();
		}

		GetCardsByAccountIdQuery query = new(id);
		List<Card> cards = await mediator.Send(query, HttpContext.RequestAborted);
		return TypedResults.Ok(cards.Select(cardMapper.ToResponse).ToList());
	}

	[HttpPost(RouteCreate)]
	[EndpointSummary("Create a single account")]
	public async Task<Ok<AccountResponse>> CreateAccount([FromBody] CreateAccountRequest model)
	{
		CreateAccountCommand command = new([mapper.ToDomain(model)]);
		List<Account> accounts = await mediator.Send(command, HttpContext.RequestAborted);
		await notifier.NotifyCreated("account", accounts[0].Id);
		return TypedResults.Ok(mapper.ToResponse(accounts[0]));
	}

	[HttpPost(RouteCreateBatch)]
	[EndpointSummary("Create accounts in batch")]
	public async Task<Ok<List<AccountResponse>>> CreateAccounts([FromBody] List<CreateAccountRequest> models)
	{
		CreateAccountCommand command = new([.. models.Select(mapper.ToDomain)]);
		List<Account> accounts = await mediator.Send(command, HttpContext.RequestAborted);
		await notifier.NotifyBulkChanged("account", "created", accounts.Select(a => a.Id));
		return TypedResults.Ok(accounts.Select(mapper.ToResponse).ToList());
	}

	[HttpPut(RouteUpdate)]
	[EndpointSummary("Update a single account")]
	public async Task<Results<NoContent, NotFound>> UpdateAccount([FromRoute] Guid id, [FromBody] UpdateAccountRequest model)
	{
		// Route id is authoritative; ignore any mismatched body id (RECEIPTS-793).
		model.Id = id;
		UpdateAccountCommand command = new([mapper.ToDomain(model)]);
		bool result = await mediator.Send(command, HttpContext.RequestAborted);

		if (!result)
		{
			logger.LogWarning("Account {Id} not found for update", id);
			return TypedResults.NotFound();
		}

		await notifier.NotifyUpdated("account", id);
		return TypedResults.NoContent();
	}

	[HttpPut(RouteUpdateBatch)]
	[EndpointSummary("Update accounts in batch")]
	public async Task<Results<NoContent, NotFound>> UpdateAccounts([FromBody] List<UpdateAccountRequest> models)
	{
		UpdateAccountCommand command = new([.. models.Select(mapper.ToDomain)]);
		bool result = await mediator.Send(command, HttpContext.RequestAborted);

		if (!result)
		{
			logger.LogWarning("Accounts batch update failed — not found");
			return TypedResults.NotFound();
		}

		await notifier.NotifyBulkChanged("account", "updated", models.Select(m => m.Id));
		return TypedResults.NoContent();
	}

	[HttpDelete(RouteDelete)]
	[Authorize(Policy = "RequireAdmin")]
	[EndpointSummary("Hard-delete an account")]
	[EndpointDescription("Permanently deletes an account. Requires the Admin role. Returns 409 Conflict if any card or transaction (including soft-deleted) references this account.")]
	public async Task<Results<NoContent, NotFound, Conflict<object>>> DeleteAccount([FromRoute] Guid id)
	{
		int cardCount = await accountService.GetCardCountByAccountIdAsync(id, HttpContext.RequestAborted);
		if (cardCount > 0)
		{
			logger.LogWarning("Account {Id} cannot be deleted — {Count} cards reference it", id, cardCount);
			return TypedResults.Conflict<object>(new { message = $"Cannot delete — {cardCount} card(s) reference this account", cardCount });
		}

		// RECEIPTS-754: transactions can outlive the card that created them (a card may be
		// moved to another account), so the card guard alone is not enough. With the
		// Transactions.AccountId FK now Restrict, deleting an account that still owns
		// transactions would fail at the database; reject it up front with a 409 instead.
		// IgnoreQueryFilters (in the repository) counts soft-deleted transactions too.
		int transactionCount = await accountService.GetTransactionCountByAccountIdAsync(id, HttpContext.RequestAborted);
		if (transactionCount > 0)
		{
			logger.LogWarning("Account {Id} cannot be deleted — {Count} transactions reference it", id, transactionCount);
			return TypedResults.Conflict<object>(new { message = $"Cannot delete — {transactionCount} transaction(s) reference this account", transactionCount });
		}

		DeleteAccountCommand command = new(id);
		bool result = await mediator.Send(command, HttpContext.RequestAborted);

		if (!result)
		{
			logger.LogWarning("Account {Id} not found for deletion", id);
			return TypedResults.NotFound();
		}

		await notifier.NotifyDeleted("account", id);
		return TypedResults.NoContent();
	}
}
