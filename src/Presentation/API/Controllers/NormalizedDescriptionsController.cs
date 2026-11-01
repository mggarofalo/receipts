using System.Security.Claims;
using API.Generated.Dtos;
using API.Services;
using Application.Commands.NormalizedDescription.LinkTemplate;
using Application.Commands.NormalizedDescription.Merge;
using Application.Commands.NormalizedDescription.PromotePendingNames;
using Application.Commands.NormalizedDescription.Rename;
using Application.Commands.NormalizedDescription.RequeuePending;
using Application.Commands.NormalizedDescription.Split;
using Application.Commands.NormalizedDescription.UndoPendingNamePromotion;
using Application.Commands.NormalizedDescription.UpdateStatus;
using Application.Models;
using Application.Models.NormalizedDescriptions;
using Application.Queries.NormalizedDescription.GetAll;
using Application.Queries.NormalizedDescription.GetById;
using Application.Queries.NormalizedDescription.GetEmbeddingCoverage;
using Application.Queries.NormalizedDescription.ListPendingNamePromotions;
using Application.Queries.NormalizedDescription.PreviewPendingNamePromotion;
using Application.Queries.NormalizedDescription.PreviewRequeuePending;
using Asp.Versioning;
using Domain.NormalizedDescriptions;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using DomainStatus = Domain.NormalizedDescriptions.NormalizedDescriptionStatus;
using DtoStatus = API.Generated.Dtos.NormalizedDescriptionStatus;

namespace API.Controllers;

// Admin-only surface for the canonical-descriptions registry (RECEIPTS-579). Every endpoint
// gates on RequireAdmin because editing the registry is an operator task, not an end-user one.
[ApiVersion("1.0")]
[ApiController]
[Route("api/normalized-descriptions")]
[Authorize(Policy = "RequireAdmin")]
public class NormalizedDescriptionsController(IMediator mediator) : ControllerBase
{
	public const string RouteEmbeddingCoverage = "embedding-coverage";
	public const string RouteGetAll = "";
	public const string RouteGetById = "{id}";
	public const string RouteMerge = "{id}/merge";
	public const string RouteSplit = "{id}/split";
	public const string RouteUpdateStatus = "{id}/status";
	public const string RouteRename = "{id}/rename";
	public const string RouteLinkTemplate = "{id}/link-template";
	public const string RouteRequeuePending = "requeue-pending";
	public const string RouteRequeuePendingPreview = "requeue-pending/preview";
	public const string RoutePromotions = "promotions";
	public const string RoutePromotionPreview = "promotions/preview";
	public const string RoutePromotionUndo = "promotions/{id}/undo";

	public const string IdCannotBeEmpty = "id must not be empty";
	public const string DiscardIdCannotBeEmpty = "discardId must not be empty";
	public const string ReceiptItemIdCannotBeEmpty = "receiptItemId must not be empty";
	public const string SplitRequiresAtLeastOneItem = "receiptItemIds must contain at least one id";
	public const string SplitNameRequired = "canonicalName must not be empty";
	public const string MergeIdsMustDiffer = "keep id and discardId must differ";
	public const string InvalidStatusFilter = "status must be 'Active', 'PendingReview' or 'Rejected' when provided";
	public const string ItemTemplateIdCannotBeEmpty = "itemTemplateId must not be empty";
	public const string OffsetOutOfRange = "offset must be >= 0";
	public const string LimitOutOfRange = "limit must be between 1 and 200";

	// Bound lives here rather than in the service: it is an HTTP input rule, and Presentation
	// must not reach into Infrastructure for a constant.
	public const int MaxPageSize = 200;
	public const string ExpectedFingerprintRequired = "expectedFingerprint must not be empty";
	public const string PromotableSetChanged = "The set of names eligible for promotion changed since it was previewed. Nothing was promoted. Re-read the preview and try again.";
	public const string PromotionAlreadyUndone = "This promotion was already undone.";
	public const string PendingSetChanged = "The set of pending-review descriptions changed since it was previewed. Nothing was deleted — re-read the preview and try again.";

	[HttpGet(RouteEmbeddingCoverage)]
	[EndpointSummary("Get semantic-vector rebuild coverage")]
	[EndpointDescription("Reports current-fingerprint coverage while the bounded background rebuild runs. Exact name matching remains available during rebuild; semantic readers ignore obsolete vectors. Admin-only.")]
	public async Task<Ok<EmbeddingCoverageResponse>> GetEmbeddingCoverage(CancellationToken cancellationToken)
	{
		EmbeddingCoverage coverage = await mediator.Send(new GetEmbeddingCoverageQuery(), cancellationToken);
		return TypedResults.Ok(new EmbeddingCoverageResponse
		{
			Fingerprint = coverage.Fingerprint,
			CanonicalReady = coverage.CanonicalReady,
			CanonicalTotal = coverage.CanonicalTotal,
			CanonicalPending = coverage.CanonicalPending,
			ItemReady = coverage.ItemReady,
			ItemTotal = coverage.ItemTotal,
			ItemPending = coverage.ItemPending,
			IsComplete = coverage.IsComplete,
		});
	}

	[HttpGet(RouteGetAll)]
	[EndpointSummary("List normalized descriptions")]
	[EndpointDescription("Returns one page of canonical normalized-description rows, optionally filtered by status and search term. Search matches the display name and the matched text. Admin-only.")]
	public async Task<Results<Ok<NormalizedDescriptionListResponse>, BadRequestProblem>> GetAllNormalizedDescriptions(
		[FromQuery] string[]? status,
		[FromQuery] string? q,
		[FromQuery] int offset = 0,
		[FromQuery] int limit = 50,
		CancellationToken cancellationToken = default)
	{
		// Repeatable since RECEIPTS-878. The merge dialog needs Active *and* PendingReview in one
		// list — a single-valued filter forced it to pick one, so two near-duplicate pending
		// entries from the same resolver batch could not be merged with each other.
		List<DomainStatus>? filter = null;
		if (status is { Length: > 0 })
		{
			HashSet<DomainStatus> parsedStatuses = [];
			// Split on commas as well as honouring repeated params: ?status=Active,PendingReview
			// is what a hand-written URL tends to look like, and ASP.NET Core binds that as one
			// element rather than two.
			foreach (string token in status.SelectMany(s => s.Split(',')))
			{
				if (string.IsNullOrWhiteSpace(token))
				{
					continue;
				}

				// Parse case-insensitively so the URL is tolerant of "active", "Active", "ACTIVE" —
				// the spec enumerates PascalCase to stay symmetric with the response-body enum,
				// but query params traditionally flex on case.
				if (!Enum.TryParse(token.Trim(), ignoreCase: true, out DomainStatus parsed))
				{
					return ApiProblem.BadRequest(InvalidStatusFilter);
				}

				parsedStatuses.Add(parsed);
			}

			// An all-whitespace filter is "no filter", not "match nothing" — an empty set would
			// silently return zero rows for what reads like an unfiltered request.
			if (parsedStatuses.Count > 0)
			{
				filter = [.. parsedStatuses];
			}
		}

		if (offset < 0)
		{
			return ApiProblem.BadRequest(OffsetOutOfRange);
		}

		if (limit < 1 || limit > MaxPageSize)
		{
			return ApiProblem.BadRequest(LimitOutOfRange);
		}

		GetAllNormalizedDescriptionsQuery query = new(filter, q, offset, limit);
		PagedResult<NormalizedDescriptionDetail> page = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new NormalizedDescriptionListResponse
		{
			Items = [.. page.Data.Select(ToResponse)],
			// The matching row count, not the page length. Clients page against this.
			TotalCount = page.Total,
			Offset = page.Offset,
			Limit = page.Limit,
		});
	}

	[HttpGet(RouteGetById)]
	[EndpointSummary("Get a normalized description by ID")]
	[EndpointDescription("Returns a single canonical normalized-description row by GUID. Admin-only.")]
	public async Task<Results<Ok<NormalizedDescriptionResponse>, NotFound, BadRequestProblem>> GetNormalizedDescriptionById(
		[FromRoute] Guid id,
		CancellationToken cancellationToken)
	{
		if (id == Guid.Empty)
		{
			return ApiProblem.BadRequest(IdCannotBeEmpty);
		}

		GetNormalizedDescriptionByIdQuery query = new(id);
		NormalizedDescriptionDetail? result = await mediator.Send(query, cancellationToken);
		if (result is null)
		{
			return TypedResults.NotFound();
		}

		return TypedResults.Ok(ToResponse(result));
	}

	[HttpPost(RouteMerge)]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
	[EndpointSummary("Merge two normalized descriptions")]
	[EndpointDescription("Re-links every live ReceiptItem currently pointing at discardId to the canonical row identified by the path {id}, then deletes the discarded row. Returns the count of re-linked items, which is legitimately zero when the discarded row had no live items. Returns 404 if either id does not exist. Admin-only.")]
	public async Task<Results<Ok<MergeNormalizedDescriptionsResponse>, BadRequestProblem, NotFoundProblem, ConflictProblem>> MergeNormalizedDescriptions(
		[FromRoute] Guid id,
		[FromBody] MergeNormalizedDescriptionRequest request,
		CancellationToken cancellationToken)
	{
		if (id == Guid.Empty)
		{
			return ApiProblem.BadRequest(IdCannotBeEmpty);
		}

		if (request.DiscardId == Guid.Empty)
		{
			return ApiProblem.BadRequest(DiscardIdCannotBeEmpty);
		}

		if (id == request.DiscardId)
		{
			return ApiProblem.BadRequest(MergeIdsMustDiffer);
		}

		MergeNormalizedDescriptionsCommand command = new(id, request.DiscardId);
		int itemsRelinkedCount;
		try
		{
			itemsRelinkedCount = await mediator.Send(command, cancellationToken);
		}
		catch (KeyNotFoundException ex)
		{
			// The registry list this id came from can be minutes old, so a merge against a row
			// somebody else already consolidated is routine. Answering 200 with a zero count made
			// it indistinguishable from a merge that genuinely had nothing to re-link, and the
			// row the admin thought they were consolidating was still there (RECEIPTS-891).
			return ApiProblem.NotFound(ex.Message);
		}
		return TypedResults.Ok(new MergeNormalizedDescriptionsResponse { ItemsRelinkedCount = itemsRelinkedCount });
	}

	[HttpPost(RouteSplit)]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
	[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
	[EndpointSummary("Detach a receipt item from its normalized description")]
	[EndpointDescription("Creates a new canonical NormalizedDescription row for the supplied ReceiptItem's raw description and re-points the item at it. Used to unpick bad auto-merges. Admin-only.")]
	public async Task<Results<Ok<NormalizedDescriptionResponse>, NotFound, BadRequestProblem, ForbiddenProblem, ConflictProblem>> SplitNormalizedDescription(
		[FromRoute] Guid id,
		[FromBody] SplitNormalizedDescriptionRequest request,
		CancellationToken cancellationToken)
	{
		if (id == Guid.Empty)
		{
			return ApiProblem.BadRequest(IdCannotBeEmpty);
		}

		if (request.ReceiptItemIds is null || request.ReceiptItemIds.Count == 0)
		{
			return ApiProblem.BadRequest(SplitRequiresAtLeastOneItem);
		}

		if (request.ReceiptItemIds.Any(itemId => itemId == Guid.Empty))
		{
			return ApiProblem.BadRequest(ReceiptItemIdCannotBeEmpty);
		}

		if (string.IsNullOrWhiteSpace(request.CanonicalName))
		{
			return ApiProblem.BadRequest(SplitNameRequired);
		}

		SplitNormalizedDescriptionCommand command = new([.. request.ReceiptItemIds], request.CanonicalName);
		try
		{
			NormalizedDescriptionDetail created = await mediator.Send(command, cancellationToken);
			return TypedResults.Ok(ToResponse(created));
		}
		catch (KeyNotFoundException)
		{
			// The service throws when any ReceiptItem does not exist — surface it as a 404.
			// We don't translate every exception type because that would mask genuine server
			// errors (DB failures, embedding service outages) behind a 404; only this one
			// maps cleanly to a client-facing 404.
			return TypedResults.NotFound();
		}
	}

	[HttpPatch(RouteUpdateStatus)]
	[EndpointSummary("Update the status of a normalized description")]
	[EndpointDescription("Flips a NormalizedDescription between Active and PendingReview. Returns 204 on success and 404 when the row does not exist. Admin-only.")]
	public async Task<Results<NoContent, NotFound, BadRequestProblem>> UpdateNormalizedDescriptionStatus(
		[FromRoute] Guid id,
		[FromBody] UpdateNormalizedDescriptionStatusRequest request,
		CancellationToken cancellationToken)
	{
		if (id == Guid.Empty)
		{
			return ApiProblem.BadRequest(IdCannotBeEmpty);
		}

		DomainStatus domainStatus = request.Status switch
		{
			DtoStatus.Active => DomainStatus.Active,
			DtoStatus.PendingReview => DomainStatus.PendingReview,
			DtoStatus.Rejected => DomainStatus.Rejected,
			_ => throw new InvalidOperationException($"Unhandled status value: {request.Status}"),
		};

		// The UpdateStatusAsync service returns false for both "row missing" and "row already at
		// target status". To preserve REST semantics, do an existence check first so we can
		// reliably return 404 for the missing case and 204 for both a real flip and a no-op.
		GetNormalizedDescriptionByIdQuery existsQuery = new(id);
		NormalizedDescriptionDetail? existing = await mediator.Send(existsQuery, cancellationToken);
		if (existing is null)
		{
			return TypedResults.NotFound();
		}

		UpdateNormalizedDescriptionStatusCommand command = new(id, domainStatus);
		await mediator.Send(command, cancellationToken);
		return TypedResults.NoContent();
	}

	[HttpPatch(RouteRename)]
	[EndpointSummary("Set or clear a normalized description's display label")]
	[EndpointDescription("Changes only the display label — never canonicalName, never the embedding — so a rename cannot change which receipt text resolves to this row. Send a null displayLabel to clear it. Returns 409 when another row already displays that name. Admin-only.")]
	public async Task<Results<Ok<NormalizedDescriptionResponse>, NotFoundProblem, BadRequestProblem, ConflictProblem>> RenameNormalizedDescription(
		[FromRoute] Guid id,
		[FromBody] RenameNormalizedDescriptionRequest request,
		CancellationToken cancellationToken)
	{
		if (id == Guid.Empty)
		{
			return ApiProblem.BadRequest(IdCannotBeEmpty);
		}

		RenameNormalizedDescriptionCommand command = new(id, request.DisplayLabel);
		try
		{
			NormalizedDescriptionDetail renamed = await mediator.Send(command, cancellationToken);
			return TypedResults.Ok(ToResponse(renamed));
		}
		catch (KeyNotFoundException ex)
		{
			return ApiProblem.NotFound(ex.Message);
		}
		catch (ArgumentException ex)
		{
			// Whitespace-only or over-long label. Distinct from the null case, which is a
			// deliberate clear and succeeds.
			return ApiProblem.BadRequest(ex.Message);
		}
		catch (InvalidOperationException ex)
		{
			// Another row already displays this name. 409 rather than 400: the request is
			// well-formed, it just lost a race with the current state of the registry.
			return ApiProblem.Conflict(ex.Message);
		}
	}

	[HttpPost(RouteLinkTemplate)]
	[EndpointSummary("Record that this row is an existing item template's item")]
	[EndpointDescription("Points the template's foreign key at its own canonical entry and consolidates the row at {id} into it, deleting {id} unless it already was that entry. Returns the surviving row, the items re-linked, and which of the two happened. Admin-only.")]
	public async Task<Results<Ok<LinkItemTemplateResponse>, BadRequestProblem, NotFoundProblem>> LinkItemTemplateToNormalizedDescription(
		[FromRoute] Guid id,
		[FromBody] LinkItemTemplateRequest request,
		CancellationToken cancellationToken)
	{
		if (id == Guid.Empty)
		{
			return ApiProblem.BadRequest(IdCannotBeEmpty);
		}

		if (request.ItemTemplateId == Guid.Empty)
		{
			return ApiProblem.BadRequest(ItemTemplateIdCannotBeEmpty);
		}

		LinkItemTemplateCommand command = new(id, request.ItemTemplateId);
		try
		{
			LinkTemplateResult result = await mediator.Send(command, cancellationToken);
			return TypedResults.Ok(new LinkItemTemplateResponse
			{
				Description = ToResponse(result.Survivor),
				ItemsRelinkedCount = result.ItemsRelinkedCount,
				Merged = result.Merged,
			});
		}
		catch (KeyNotFoundException ex)
		{
			// Either the row or the template. The message names which — the review queue this id
			// came from can be minutes old, and so can the template picker, so "not found" alone
			// leaves the admin guessing which half of their request went stale.
			return ApiProblem.NotFound(ex.Message);
		}
		catch (InvalidOperationException ex)
		{
			// The row is a tombstone. 400 rather than 409: this is not a lost race, it is a
			// request that would undo a decision the endpoint refuses to undo on the caller's
			// behalf, and retrying unchanged will never succeed.
			return ApiProblem.BadRequest(ex.Message);
		}
	}

	[HttpGet(RouteRequeuePendingPreview)]
	[EndpointSummary("Preview what a requeue of pending descriptions would destroy")]
	[EndpointDescription("Reports the pending rows that would be deleted, and the live receipt items that would be unlinked. Read-only. Admin-only.")]
	public async Task<Ok<RequeuePendingPreviewResponse>> PreviewRequeuePending(CancellationToken cancellationToken)
	{
		RequeuePendingPreview preview = await mediator.Send(new PreviewRequeuePendingQuery(), cancellationToken);
		return TypedResults.Ok(new RequeuePendingPreviewResponse
		{
			PendingDescriptionCount = preview.PendingDescriptionCount,
			PendingFingerprint = preview.PendingFingerprint,
			LinkedItemCount = preview.LinkedItemCount,
		});
	}

	[HttpPost(RouteRequeuePending)]
	[EndpointSummary("Delete every pending-review description so the resolver rebuilds it")]
	[EndpointDescription("Deletes all PendingReview rows and, in the same transaction, nulls the FK on every receipt item that pointed at one. Returns 409 when expectedFingerprint no longer matches the live pending set. Admin-only.")]
	public async Task<Results<Ok<RequeuePendingResponse>, BadRequestProblem, ConflictProblem>> RequeuePending(
		[FromBody] RequeuePendingRequest request,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
		{
			return ApiProblem.BadRequest(ExpectedFingerprintRequired);
		}

		RequeuePendingCommand command = new(request.ExpectedFingerprint);
		RequeuePendingResult? result = await mediator.Send(command, cancellationToken);

		// A null result is the optimistic-concurrency guard tripping, not a failure to act: the
		// live pending set moved between preview and confirm, so nothing was deleted. 409 tells
		// the client to re-read rather than retry blindly.
		if (result is null)
		{
			return ApiProblem.Conflict(PendingSetChanged);
		}
		return TypedResults.Ok(new RequeuePendingResponse
		{
			DeletedDescriptionCount = result.DeletedDescriptionCount,
			UnlinkedItemCount = result.UnlinkedItemCount,
		});
	}

	[HttpGet(RoutePromotionPreview)]
	[EndpointSummary("Preview which pending-review names a promotion would make active")]
	[EndpointDescription("Lists PendingReview names with enough live purchases, no source key shared with another name and no name collision. Read-only. Admin-only.")]
	public async Task<Ok<PendingNamePromotionPreviewResponse>> PreviewPendingNamePromotion(CancellationToken cancellationToken)
	{
		PendingNamePromotionPreview preview = await mediator.Send(new PreviewPendingNamePromotionQuery(), cancellationToken);
		return TypedResults.Ok(new PendingNamePromotionPreviewResponse
		{
			MinimumPurchases = preview.MinimumPurchases,
			NameCount = preview.Names.Count,
			PurchaseCount = preview.PurchaseCount,
			Fingerprint = preview.Fingerprint,
			Names = preview.Names.Select(name => new PromotableNameResponse
			{
				Id = name.Id,
				CanonicalName = name.CanonicalName,
				PurchaseCount = name.PurchaseCount,
			}).ToList(),
		});
	}

	[HttpGet(RoutePromotions)]
	[EndpointSummary("List recent pending-name promotions")]
	[EndpointDescription("The 20 most recent promotions, newest first, with their undo state. Admin-only.")]
	public async Task<Ok<List<PendingNamePromotionResponse>>> ListPendingNamePromotions(CancellationToken cancellationToken)
	{
		IReadOnlyList<PendingNamePromotion> promotions = await mediator.Send(new ListPendingNamePromotionsQuery(), cancellationToken);
		return TypedResults.Ok(promotions.Select(promotion => new PendingNamePromotionResponse
		{
			Id = promotion.Id,
			CreatedBy = promotion.CreatedBy,
			CreatedAt = promotion.CreatedAt,
			PromotedCount = promotion.PromotedCount,
			PurchaseCount = promotion.PurchaseCount,
			UndoneAt = promotion.UndoneAt,
			UndoneBy = promotion.UndoneBy,
			RestoredCount = promotion.RestoredCount,
			KeptCount = promotion.KeptCount,
		}).ToList());
	}

	[HttpPost(RoutePromotions)]
	[EndpointSummary("Promote the previewed pending-review names to active")]
	[EndpointDescription("Makes every previewed name Active and records the promotion for undo. Purchases keep their links and review state. Returns 409 when expectedFingerprint no longer matches the eligible set. Admin-only.")]
	public async Task<Results<Ok<PromotePendingNamesResponse>, BadRequestProblem, ConflictProblem>> PromotePendingNames(
		[FromBody] PromotePendingNamesRequest request,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
		{
			return ApiProblem.BadRequest(ExpectedFingerprintRequired);
		}

		PendingNamePromotionResult? result = await mediator.Send(
			new PromotePendingNamesCommand(Actor, request.ExpectedFingerprint), cancellationToken);
		if (result is null)
		{
			return ApiProblem.Conflict(PromotableSetChanged);
		}
		return TypedResults.Ok(new PromotePendingNamesResponse
		{
			PromotionId = result.PromotionId,
			PromotedCount = result.PromotedCount,
			PurchaseCount = result.PurchaseCount,
		});
	}

	[HttpPost(RoutePromotionUndo)]
	[EndpointSummary("Return a promotion's names to pending review")]
	[EndpointDescription("Returns each promoted name to PendingReview while it is unchanged since the promotion; changed names stay active and are counted as kept. Admin-only.")]
	public async Task<Results<Ok<UndoPendingNamePromotionResponse>, NotFound, ConflictProblem>> UndoPendingNamePromotion(
		[FromRoute] Guid id,
		CancellationToken cancellationToken)
	{
		PendingNamePromotionUndo undo = await mediator.Send(new UndoPendingNamePromotionCommand(Actor, id), cancellationToken);
		return undo.Outcome switch
		{
			PendingNamePromotionUndoOutcome.NotFound => TypedResults.NotFound(),
			PendingNamePromotionUndoOutcome.AlreadyUndone => ApiProblem.Conflict(PromotionAlreadyUndone),
			_ => TypedResults.Ok(new UndoPendingNamePromotionResponse { RestoredCount = undo.RestoredCount, KeptCount = undo.KeptCount }),
		};
	}

	private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

	private static NormalizedDescriptionResponse ToResponse(NormalizedDescriptionDetail detail)
	{
		NormalizedDescription source = detail.Description;
		return new NormalizedDescriptionResponse
		{
			Id = source.Id,
			CanonicalName = source.CanonicalName,
			DisplayLabel = source.DisplayLabel,
			DisplayName = source.DisplayName,
			Status = source.Status switch
			{
				DomainStatus.Active => DtoStatus.Active,
				DomainStatus.PendingReview => DtoStatus.PendingReview,
				DomainStatus.Rejected => DtoStatus.Rejected,
				_ => throw new InvalidOperationException($"Unhandled status value: {source.Status}"),
			},
			CreatedAt = source.CreatedAt,
			LinkedItemCount = detail.LinkedItemCount,
			LastSeen = detail.LastSeen,
			SampleRawDescriptions = [.. detail.SampleRawDescriptions],
			// Id and name travel together — both null when no live template declares this row
			// (RECEIPTS-930). The count is separate because one name plus "and 2 others" is a
			// different statement from one name alone, and merging template-backed entries
			// routinely produces the second.
			LinkedTemplateId = detail.LinkedTemplateId,
			LinkedTemplateName = detail.LinkedTemplateName,
			LinkedTemplateCount = detail.LinkedTemplateCount,
			// Both neighbour fields stay null together when nothing was recorded — the contract
			// says clients must read that as "no comparison recorded" rather than a zero score
			// (RECEIPTS-873).
			NearestNeighbourName = detail.NearestNeighbourName,
			NearestNeighbourSimilarity = source.NearestNeighbourSimilarity,
		};
	}
}
