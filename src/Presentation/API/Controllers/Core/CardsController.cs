using API.Generated.Dtos;
using API.Mapping.Core;
using API.Services;
using Application.Commands.Card.Create;
using Application.Commands.Card.Delete;
using Application.Commands.Card.Merge;
using Application.Commands.Card.Update;
using Application.Interfaces.Services;
using Application.Models;
using Application.Models.Merge;
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
[Route("api/cards")]
[Produces("application/json")]
[Authorize]
public class CardsController(IMediator mediator, CardMapper mapper, ILogger<CardsController> logger, IEntityChangeNotifier notifier, ICardService cardService, IAccountService accountService) : ControllerBase
{
	public const string RouteGetById = "{id}";
	public const string RouteGetAll = "";
	public const string RouteCreate = "";
	public const string RouteCreateBatch = "batch";
	public const string RouteUpdate = "{id}";
	public const string RouteUpdateBatch = "batch";
	public const string RouteDelete = "{id}";
	public const string RouteMerge = "merge";
	public const string RouteMergePreview = "merge/preview";

	public const string SourceCardIdsRequired = "sourceCardIds must contain at least 1 card id";

	[HttpGet(RouteGetById)]
	[EndpointSummary("Get a card by ID")]
	[EndpointDescription("Returns a single card matching the provided GUID.")]
	public async Task<Results<Ok<CardResponse>, NotFound>> GetCardById([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		GetCardByIdQuery query = new(id);
		Card? result = await mediator.Send(query, cancellationToken);

		if (result == null)
		{
			logger.LogWarning("Card {Id} not found", id);
			return TypedResults.NotFound();
		}

		CardResponse model = mapper.ToResponse(result);
		return TypedResults.Ok(model);
	}

	[HttpGet(RouteGetAll)]
	[EndpointSummary("Get all cards")]
	public async Task<Results<Ok<CardListResponse>, BadRequest<ProblemDetails>>> GetAllCards([FromQuery] bool? isActive = null, [FromQuery] string? q = null, [FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null, CancellationToken cancellationToken = default)
	{
		if (offset < 0)
		{
			return ApiProblem.BadRequest("offset must be >= 0");
		}

		if (limit <= 0 || limit > 500)
		{
			return ApiProblem.BadRequest("limit must be between 1 and 500");
		}

		if (sortBy is not null && !SortableColumns.Card.Contains(sortBy))
		{
			return ApiProblem.BadRequest($"Invalid sortBy '{sortBy}'. Allowed: {string.Join(", ", SortableColumns.Card)}");
		}

		if (!SortableColumns.IsValidDirection(sortDirection))
		{
			return ApiProblem.BadRequest($"Invalid sortDirection '{sortDirection}'. Allowed: asc, desc");
		}

		SortParams sort = new(sortBy, sortDirection);
		GetAllCardsQuery query = new(offset, limit, sort, isActive, q);
		PagedResult<Card> result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new CardListResponse
		{
			Data = [.. result.Data.Select(mapper.ToResponse)],
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}

	[HttpPost(RouteCreate)]
	[EndpointSummary("Create a single card")]
	public async Task<Results<Ok<CardResponse>, BadRequest<ProblemDetails>>> CreateCard([FromBody] CreateCardRequest model, CancellationToken cancellationToken = default)
	{
		if (await MissingAccountIdAsync(model.AccountId, cancellationToken))
		{
			logger.LogWarning("Card create rejected — parent account {AccountId} not found", model.AccountId);
			return ApiProblem.BadRequest($"Account {model.AccountId} does not exist.");
		}

		CreateCardCommand command = new([mapper.ToDomain(model)]);
		List<Card> cards = await mediator.Send(command, cancellationToken);
		await notifier.NotifyCreated("card", cards[0].Id);
		return TypedResults.Ok(mapper.ToResponse(cards[0]));
	}

	[HttpPost(RouteCreateBatch)]
	[EndpointSummary("Create cards in batch")]
	public async Task<Results<Ok<List<CardResponse>>, BadRequest<ProblemDetails>>> CreateCards([FromBody] List<CreateCardRequest> models, CancellationToken cancellationToken = default)
	{
		Guid? missing = await FirstMissingAccountIdAsync(models.Select(m => m.AccountId), cancellationToken);
		if (missing.HasValue)
		{
			logger.LogWarning("Cards batch create rejected — parent account {AccountId} not found", missing.Value);
			return ApiProblem.BadRequest($"Account {missing.Value} does not exist.");
		}

		CreateCardCommand command = new([.. models.Select(mapper.ToDomain)]);
		List<Card> cards = await mediator.Send(command, cancellationToken);
		await notifier.NotifyBulkChanged("card", "created", cards.Select(a => a.Id));
		return TypedResults.Ok(cards.Select(mapper.ToResponse).ToList());
	}

	[HttpPut(RouteUpdate)]
	[EndpointSummary("Update a single card")]
	public async Task<Results<NoContent, NotFound, BadRequest<ProblemDetails>>> UpdateCard([FromRoute] Guid id, [FromBody] UpdateCardRequest model, CancellationToken cancellationToken = default)
	{
		if (await MissingAccountIdAsync(model.AccountId, cancellationToken))
		{
			logger.LogWarning("Card {Id} update rejected — parent account {AccountId} not found", id, model.AccountId);
			return ApiProblem.BadRequest($"Account {model.AccountId} does not exist.");
		}

		// Route id is authoritative; ignore any mismatched body id (RECEIPTS-793).
		model.Id = id;
		UpdateCardCommand command = new([mapper.ToDomain(model)]);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("Card {Id} not found for update", id);
			return TypedResults.NotFound();
		}

		await notifier.NotifyUpdated("card", id);
		return TypedResults.NoContent();
	}

	[HttpPut(RouteUpdateBatch)]
	[EndpointSummary("Update cards in batch")]
	public async Task<Results<NoContent, NotFound, BadRequest<ProblemDetails>>> UpdateCards([FromBody] List<UpdateCardRequest> models, CancellationToken cancellationToken = default)
	{
		Guid? missing = await FirstMissingAccountIdAsync(models.Select(m => m.AccountId), cancellationToken);
		if (missing.HasValue)
		{
			logger.LogWarning("Cards batch update rejected — parent account {AccountId} not found", missing.Value);
			return ApiProblem.BadRequest($"Account {missing.Value} does not exist.");
		}

		UpdateCardCommand command = new([.. models.Select(mapper.ToDomain)]);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("Cards batch update failed — not found");
			return TypedResults.NotFound();
		}

		await notifier.NotifyBulkChanged("card", "updated", models.Select(m => m.Id));
		return TypedResults.NoContent();
	}

	private async Task<bool> MissingAccountIdAsync(Guid accountId, CancellationToken cancellationToken)
	{
		return !await accountService.ExistsAsync(accountId, cancellationToken);
	}

	private async Task<Guid?> FirstMissingAccountIdAsync(IEnumerable<Guid> accountIds, CancellationToken cancellationToken)
	{
		foreach (Guid accountId in accountIds.Distinct())
		{
			if (!await accountService.ExistsAsync(accountId, cancellationToken))
			{
				return accountId;
			}
		}

		return null;
	}

	[HttpDelete(RouteDelete)]
	[Authorize(Policy = "RequireAdmin")]
	[EndpointSummary("Hard-delete a card")]
	[EndpointDescription("Permanently deletes a card. Requires the Admin role. Returns 409 Conflict if transactions reference this card.")]
	public async Task<Results<NoContent, NotFound, Conflict<ProblemDetails>>> DeleteCard([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		int transactionCount = await cardService.GetTransactionCountByCardIdAsync(id, cancellationToken);
		if (transactionCount > 0)
		{
			logger.LogWarning("Card {Id} cannot be deleted — {Count} transactions reference it", id, transactionCount);
			return ApiProblem.Conflict(
				$"Cannot delete — {transactionCount} transaction(s) reference this card",
				new Dictionary<string, object?> { ["transactionCount"] = transactionCount });
		}

		DeleteCardCommand command = new(id);
		bool result = await mediator.Send(command, cancellationToken);

		if (!result)
		{
			logger.LogWarning("Card {Id} not found for deletion", id);
			return TypedResults.NotFound();
		}

		await notifier.NotifyDeleted("card", id);
		return TypedResults.NoContent();
	}

	[HttpPost(RouteMerge)]
	[Authorize(Policy = "RequireAdmin")]
	[EndpointSummary("Merge cards into a target account")]
	[EndpointDescription("Repoints the listed cards (and their transactions) to the target account and deletes any now-orphaned accounts. Requires the Admin role. Returns 409 Conflict if the source/target accounts have differing YNAB account mappings; resubmit with ynabMappingWinnerAccountId to resolve.")]
	public async Task<Results<Ok<MergeCardsResponse>, BadRequest<ProblemDetails>, NotFound, Conflict<MergeCardsConflictResponse>>> MergeCards([FromBody] MergeCardsRequest model, CancellationToken cancellationToken = default)
	{
		if (model.SourceCardIds is null || model.SourceCardIds.Count == 0)
		{
			return ApiProblem.BadRequest(SourceCardIdsRequired);
		}

		MergeCardsIntoAccountCommand command;
		try
		{
			command = new MergeCardsIntoAccountCommand(
				model.TargetAccountId,
				[.. model.SourceCardIds],
				model.YnabMappingWinnerAccountId);
		}
		catch (ArgumentException ex)
		{
			return ApiProblem.BadRequest(ex.Message);
		}

		MergeCardsResult result;
		try
		{
			result = await mediator.Send(command, cancellationToken);
		}
		catch (KeyNotFoundException ex)
		{
			logger.LogWarning("Merge failed — {Message}", ex.Message);
			return TypedResults.NotFound();
		}
		catch (ArgumentException ex)
		{
			return ApiProblem.BadRequest(ex.Message);
		}

		if (result.Conflicts is not null)
		{
			MergeCardsConflictResponse body = new()
			{
				Message = "Source cards have differing YNAB mappings. Resubmit with ynabMappingWinnerAccountId set to the account whose mapping should be retained.",
				Conflicts = [.. result.Conflicts.Select(c => new API.Generated.Dtos.YnabMappingConflict
				{
					AccountId = c.AccountId,
					AccountName = c.AccountName,
					YnabBudgetId = c.YnabBudgetId,
					YnabAccountId = c.YnabAccountId,
					YnabAccountName = c.YnabAccountName,
				})],
			};
			return TypedResults.Conflict(body);
		}

		// A no-op merge wrote nothing, so there is nothing for connected clients to refetch —
		// broadcasting "cards updated" would be the same lie the response used to tell.
		if (result.IsNoOp)
		{
			logger.LogInformation(
				"Merge into account {TargetAccountId} changed nothing — all {CardCount} selected cards already belonged to it",
				model.TargetAccountId,
				model.SourceCardIds.Count);
		}
		else
		{
			await notifier.NotifyBulkChanged("card", "updated", model.SourceCardIds);
		}

		return TypedResults.Ok(new MergeCardsResponse
		{
			AccountsRemoved = result.AccountsRemoved,
			CardsMoved = result.CardsMoved,
			TransactionsRepointed = result.TransactionsRepointed,
		});
	}

	[HttpPost(RouteMergePreview)]
	[Authorize(Policy = "RequireAdmin")]
	[EndpointSummary("Preview what a card merge would do")]
	[EndpointDescription("Runs the merge's validation and reports its impact without writing anything. Requires the Admin role. Rejects exactly what the merge itself would reject.")]
	public async Task<Results<Ok<MergeCardsPreviewResponse>, BadRequest<ProblemDetails>, NotFound>> PreviewMergeCards(
		[FromBody] MergeCardsPreviewRequest model,
		CancellationToken cancellationToken = default)
	{
		if (model.SourceCardIds is null || model.SourceCardIds.Count == 0)
		{
			return ApiProblem.BadRequest(SourceCardIdsRequired);
		}

		PreviewMergeCardsQuery query;
		try
		{
			query = new PreviewMergeCardsQuery(
				model.TargetAccountId,
				[.. model.SourceCardIds],
				model.YnabMappingWinnerAccountId);
		}
		catch (ArgumentException ex)
		{
			return ApiProblem.BadRequest(ex.Message);
		}

		MergeCardsPreview preview;
		try
		{
			preview = await mediator.Send(query, cancellationToken);
		}
		catch (KeyNotFoundException ex)
		{
			logger.LogWarning("Merge preview failed — {Message}", ex.Message);
			return TypedResults.NotFound();
		}
		catch (ArgumentException ex)
		{
			// A partial source-account selection lands here. The preview reports it the same
			// way the merge would, rather than answering with an impact that could never happen.
			return ApiProblem.BadRequest(ex.Message);
		}

		return TypedResults.Ok(new MergeCardsPreviewResponse
		{
			AccountsToRemove = [.. preview.AccountsToRemove.Select(a => new API.Generated.Dtos.MergeCardsPreviewAccount
			{
				Id = a.Id,
				Name = a.Name,
			})],
			CardsToMove = preview.CardsToMove,
			TransactionsToRepoint = preview.TransactionsToRepoint,
			TrashedTransactionsToRepoint = preview.TrashedTransactionsToRepoint,
			SurvivingYnabMapping = preview.SurvivingYnabMapping is null ? null : new API.Generated.Dtos.MergeCardsPreviewMapping
			{
				FromAccountId = preview.SurvivingYnabMapping.FromAccountId,
				FromAccountName = preview.SurvivingYnabMapping.FromAccountName,
				YnabAccountName = preview.SurvivingYnabMapping.YnabAccountName,
			},
			Conflicts = preview.Conflicts is null ? null : [.. preview.Conflicts.Select(c => new API.Generated.Dtos.YnabMappingConflict
			{
				AccountId = c.AccountId,
				AccountName = c.AccountName,
				YnabBudgetId = c.YnabBudgetId,
				YnabAccountId = c.YnabAccountId,
				YnabAccountName = c.YnabAccountName,
			})],
		});
	}
}
