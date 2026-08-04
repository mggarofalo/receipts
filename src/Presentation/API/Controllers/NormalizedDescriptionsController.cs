using API.Generated.Dtos;
using Application.Commands.NormalizedDescription.Merge;
using Application.Commands.NormalizedDescription.RequeuePending;
using Application.Commands.NormalizedDescription.Split;
using Application.Commands.NormalizedDescription.UpdateSettings;
using Application.Commands.NormalizedDescription.UpdateStatus;
using Application.Models.NormalizedDescriptions;
using Application.Queries.NormalizedDescription.GetAll;
using Application.Queries.NormalizedDescription.GetById;
using Application.Queries.NormalizedDescription.GetSettings;
using Application.Queries.NormalizedDescription.PreviewRequeuePending;
using Application.Queries.NormalizedDescription.PreviewThresholdImpact;
using Application.Queries.NormalizedDescription.TestMatch;
using Asp.Versioning;
using Domain.NormalizedDescriptions;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using DomainStatus = Domain.NormalizedDescriptions.NormalizedDescriptionStatus;
using DtoStatus = API.Generated.Dtos.NormalizedDescriptionStatus;

namespace API.Controllers;

// Admin-only surface for the canonical-descriptions registry. Wired up across two issues:
// RECEIPTS-580 scaffolds the settings/test/preview endpoints; RECEIPTS-579 extends the class
// with list/get/merge/split/status endpoints. Every endpoint gates on RequireAdmin because
// tuning thresholds, probing the classifier, and editing the registry are operator tasks,
// not end-user ones.
[ApiVersion("1.0")]
[ApiController]
[Route("api/normalized-descriptions")]
[Produces("application/json")]
[Authorize(Policy = "RequireAdmin")]
public class NormalizedDescriptionsController(IMediator mediator) : ControllerBase
{
	public const string RouteSettings = "settings";
	public const string RoutePreview = "settings/preview";
	public const string RouteTest = "test";
	public const string RouteGetAll = "";
	public const string RouteGetById = "{id}";
	public const string RouteMerge = "{id}/merge";
	public const string RouteSplit = "{id}/split";
	public const string RouteUpdateStatus = "{id}/status";
	public const string RouteRequeuePending = "requeue-pending";
	public const string RouteRequeuePendingPreview = "requeue-pending/preview";

	public const string AutoAcceptOutOfRange = "autoAcceptThreshold must be between 0 and 1";
	public const string PendingReviewOutOfRange = "pendingReviewThreshold must be between 0 and 1";
	public const string PendingMustBeLessThanAuto = "pendingReviewThreshold must be strictly less than autoAcceptThreshold";
	public const string DescriptionRequired = "description must not be empty";
	public const string TopNOutOfRange = "topN must be between 1 and 20";
	public const string OverrideOutOfRange = "threshold overrides must be between 0 and 1 when provided";
	public const string IdCannotBeEmpty = "id must not be empty";
	public const string DiscardIdCannotBeEmpty = "discardId must not be empty";
	public const string ReceiptItemIdCannotBeEmpty = "receiptItemId must not be empty";
	public const string MergeIdsMustDiffer = "keep id and discardId must differ";
	public const string InvalidStatusFilter = "status must be 'Active' or 'PendingReview' when provided";
	public const string ExpectedPendingCountNegative = "expectedPendingCount must not be negative";
	public const string PendingCountChanged = "The pending-review count changed since it was previewed. Nothing was deleted — re-read the preview and try again.";

	[HttpGet(RouteSettings)]
	[EndpointSummary("Get the current normalized-description threshold settings")]
	[EndpointDescription("Returns the live auto-accept and pending-review thresholds used by the resolver. Admin-only.")]
	public async Task<Ok<NormalizedDescriptionSettingsResponse>> GetSettings(CancellationToken cancellationToken)
	{
		NormalizedDescriptionSettings settings = await mediator.Send(new GetNormalizedDescriptionSettingsQuery(), cancellationToken);
		return TypedResults.Ok(ToResponse(settings));
	}

	[HttpPatch(RouteSettings)]
	[EndpointSummary("Update the normalized-description threshold settings")]
	[EndpointDescription("Both thresholds must satisfy 0 <= pendingReviewThreshold < autoAcceptThreshold <= 1. Admin-only.")]
	public async Task<Results<Ok<NormalizedDescriptionSettingsResponse>, BadRequest<string>>> UpdateSettings(
		[FromBody] UpdateNormalizedDescriptionSettingsRequest request,
		CancellationToken cancellationToken)
	{
		if (request.AutoAcceptThreshold < 0 || request.AutoAcceptThreshold > 1)
		{
			return TypedResults.BadRequest(AutoAcceptOutOfRange);
		}

		if (request.PendingReviewThreshold < 0 || request.PendingReviewThreshold > 1)
		{
			return TypedResults.BadRequest(PendingReviewOutOfRange);
		}

		if (request.PendingReviewThreshold >= request.AutoAcceptThreshold)
		{
			return TypedResults.BadRequest(PendingMustBeLessThanAuto);
		}

		UpdateNormalizedDescriptionSettingsCommand command = new(request.AutoAcceptThreshold, request.PendingReviewThreshold);
		NormalizedDescriptionSettings result = await mediator.Send(command, cancellationToken);
		return TypedResults.Ok(ToResponse(result));
	}

	[HttpPost(RouteTest)]
	[EndpointSummary("Probe the normalized-description classifier for a given description")]
	[EndpointDescription("Returns the top-N ANN candidates and the classification branch the resolver would take. Admin-only.")]
	public async Task<Results<Ok<MatchTestResultResponse>, BadRequest<string>>> TestMatch(
		[FromBody] TestMatchRequest request,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(request.Description))
		{
			return TypedResults.BadRequest(DescriptionRequired);
		}

		int topN = request.TopN == 0 ? 5 : request.TopN;
		if (topN < 1 || topN > 20)
		{
			return TypedResults.BadRequest(TopNOutOfRange);
		}

		if (request.AutoAcceptThresholdOverride is double autoOverride &&
			(autoOverride < 0 || autoOverride > 1))
		{
			return TypedResults.BadRequest(OverrideOutOfRange);
		}

		if (request.PendingReviewThresholdOverride is double pendingOverride &&
			(pendingOverride < 0 || pendingOverride > 1))
		{
			return TypedResults.BadRequest(OverrideOutOfRange);
		}

		// If both overrides are supplied, reject a crossed pair up front for the same reason
		// UpdateSettings does — don't wait for the service to throw.
		if (request.AutoAcceptThresholdOverride is double autoSet &&
			request.PendingReviewThresholdOverride is double pendingSet &&
			pendingSet >= autoSet)
		{
			return TypedResults.BadRequest(PendingMustBeLessThanAuto);
		}

		TestNormalizedDescriptionMatchQuery query = new(
			request.Description,
			topN,
			request.AutoAcceptThresholdOverride,
			request.PendingReviewThresholdOverride);

		MatchTestResult result = await mediator.Send(query, cancellationToken);
		return TypedResults.Ok(ToResponse(result));
	}

	[HttpPost(RoutePreview)]
	[EndpointSummary("Preview the impact of changing normalized-description thresholds")]
	[EndpointDescription("Returns current and proposed classification counts with per-bucket deltas. Admin-only.")]
	public async Task<Results<Ok<ThresholdImpactPreviewResponse>, BadRequest<string>>> PreviewThresholdImpact(
		[FromBody] PreviewThresholdImpactRequest request,
		CancellationToken cancellationToken)
	{
		if (request.AutoAcceptThreshold < 0 || request.AutoAcceptThreshold > 1)
		{
			return TypedResults.BadRequest(AutoAcceptOutOfRange);
		}

		if (request.PendingReviewThreshold < 0 || request.PendingReviewThreshold > 1)
		{
			return TypedResults.BadRequest(PendingReviewOutOfRange);
		}

		if (request.PendingReviewThreshold >= request.AutoAcceptThreshold)
		{
			return TypedResults.BadRequest(PendingMustBeLessThanAuto);
		}

		PreviewThresholdImpactQuery query = new(request.AutoAcceptThreshold, request.PendingReviewThreshold);
		ThresholdImpactPreview result = await mediator.Send(query, cancellationToken);
		return TypedResults.Ok(ToResponse(result));
	}

	[HttpGet(RouteGetAll)]
	[EndpointSummary("List normalized descriptions")]
	[EndpointDescription("Returns all canonical normalized-description rows, optionally filtered by status (Active or PendingReview). Admin-only.")]
	public async Task<Results<Ok<NormalizedDescriptionListResponse>, BadRequest<string>>> GetAllNormalizedDescriptions(
		[FromQuery] string? status,
		CancellationToken cancellationToken)
	{
		DomainStatus? filter = null;
		if (!string.IsNullOrWhiteSpace(status))
		{
			// Parse case-insensitively so the URL is tolerant of "active", "Active", "ACTIVE" —
			// the spec enumerates PascalCase to stay symmetric with the response-body enum,
			// but query params traditionally flex on case.
			if (!Enum.TryParse(status, ignoreCase: true, out DomainStatus parsed))
			{
				return TypedResults.BadRequest(InvalidStatusFilter);
			}

			filter = parsed;
		}

		GetAllNormalizedDescriptionsQuery query = new(filter);
		List<NormalizedDescriptionDetail> items = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new NormalizedDescriptionListResponse
		{
			Items = [.. items.Select(ToResponse)],
			TotalCount = items.Count,
		});
	}

	[HttpGet(RouteGetById)]
	[EndpointSummary("Get a normalized description by ID")]
	[EndpointDescription("Returns a single canonical normalized-description row by GUID. Admin-only.")]
	public async Task<Results<Ok<NormalizedDescriptionResponse>, NotFound, BadRequest<string>>> GetNormalizedDescriptionById(
		[FromRoute] Guid id,
		CancellationToken cancellationToken)
	{
		if (id == Guid.Empty)
		{
			return TypedResults.BadRequest(IdCannotBeEmpty);
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
	[EndpointSummary("Merge two normalized descriptions")]
	[EndpointDescription("Re-links every live ReceiptItem currently pointing at discardId to the canonical row identified by the path {id}, then deletes the discarded row. Returns the count of re-linked items — zero when either id was missing or the two ids were identical. Admin-only.")]
	public async Task<Results<Ok<MergeNormalizedDescriptionsResponse>, BadRequest<string>>> MergeNormalizedDescriptions(
		[FromRoute] Guid id,
		[FromBody] MergeNormalizedDescriptionRequest request,
		CancellationToken cancellationToken)
	{
		if (id == Guid.Empty)
		{
			return TypedResults.BadRequest(IdCannotBeEmpty);
		}

		if (request.DiscardId == Guid.Empty)
		{
			return TypedResults.BadRequest(DiscardIdCannotBeEmpty);
		}

		if (id == request.DiscardId)
		{
			return TypedResults.BadRequest(MergeIdsMustDiffer);
		}

		MergeNormalizedDescriptionsCommand command = new(id, request.DiscardId);
		int itemsRelinkedCount = await mediator.Send(command, cancellationToken);
		return TypedResults.Ok(new MergeNormalizedDescriptionsResponse { ItemsRelinkedCount = itemsRelinkedCount });
	}

	[HttpPost(RouteSplit)]
	[EndpointSummary("Detach a receipt item from its normalized description")]
	[EndpointDescription("Creates a new canonical NormalizedDescription row for the supplied ReceiptItem's raw description and re-points the item at it. Used to unpick bad auto-merges. Admin-only.")]
	public async Task<Results<Ok<NormalizedDescriptionResponse>, NotFound, BadRequest<string>>> SplitNormalizedDescription(
		[FromRoute] Guid id,
		[FromBody] SplitNormalizedDescriptionRequest request,
		CancellationToken cancellationToken)
	{
		if (id == Guid.Empty)
		{
			return TypedResults.BadRequest(IdCannotBeEmpty);
		}

		if (request.ReceiptItemId == Guid.Empty)
		{
			return TypedResults.BadRequest(ReceiptItemIdCannotBeEmpty);
		}

		SplitNormalizedDescriptionCommand command = new(request.ReceiptItemId);
		try
		{
			NormalizedDescriptionDetail created = await mediator.Send(command, cancellationToken);
			return TypedResults.Ok(ToResponse(created));
		}
		catch (KeyNotFoundException)
		{
			// The service throws when the ReceiptItem does not exist — surface it as a 404.
			// We don't translate every exception type because that would mask genuine server
			// errors (DB failures, embedding service outages) behind a 404; only this one
			// maps cleanly to a client-facing 404.
			return TypedResults.NotFound();
		}
	}

	[HttpPatch(RouteUpdateStatus)]
	[EndpointSummary("Update the status of a normalized description")]
	[EndpointDescription("Flips a NormalizedDescription between Active and PendingReview. Returns 204 on success and 404 when the row does not exist. Admin-only.")]
	public async Task<Results<NoContent, NotFound, BadRequest<string>>> UpdateNormalizedDescriptionStatus(
		[FromRoute] Guid id,
		[FromBody] UpdateNormalizedDescriptionStatusRequest request,
		CancellationToken cancellationToken)
	{
		if (id == Guid.Empty)
		{
			return TypedResults.BadRequest(IdCannotBeEmpty);
		}

		DomainStatus domainStatus = request.Status switch
		{
			DtoStatus.Active => DomainStatus.Active,
			DtoStatus.PendingReview => DomainStatus.PendingReview,
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

	[HttpGet(RouteRequeuePendingPreview)]
	[EndpointSummary("Preview what a requeue of pending descriptions would destroy")]
	[EndpointDescription("Reports the pending rows that would be deleted, the live receipt items that would be unlinked, the stale match scores that would be cleared, and the resolver's estimated catch-up time. Read-only. Admin-only.")]
	public async Task<Ok<RequeuePendingPreviewResponse>> PreviewRequeuePending(CancellationToken cancellationToken)
	{
		RequeuePendingPreview preview = await mediator.Send(new PreviewRequeuePendingQuery(), cancellationToken);
		return TypedResults.Ok(new RequeuePendingPreviewResponse
		{
			PendingDescriptionCount = preview.PendingDescriptionCount,
			LinkedItemCount = preview.LinkedItemCount,
			StaleMatchScoreCount = preview.StaleMatchScoreCount,
			EstimatedResolverCycles = preview.EstimatedResolverCycles,
			EstimatedCatchUpSeconds = preview.EstimatedCatchUpSeconds,
		});
	}

	[HttpPost(RouteRequeuePending)]
	[EndpointSummary("Delete every pending-review description so the resolver rebuilds it")]
	[EndpointDescription("Deletes all PendingReview rows and, in the same transaction, nulls the FK and the match score on every receipt item that pointed at one. Returns 409 when expectedPendingCount no longer matches the live count. Admin-only.")]
	public async Task<Results<Ok<RequeuePendingResponse>, BadRequest<string>, Conflict<string>>> RequeuePending(
		[FromBody] RequeuePendingRequest request,
		CancellationToken cancellationToken)
	{
		if (request.ExpectedPendingCount < 0)
		{
			return TypedResults.BadRequest(ExpectedPendingCountNegative);
		}

		RequeuePendingCommand command = new(request.ExpectedPendingCount);
		RequeuePendingResult? result = await mediator.Send(command, cancellationToken);

		// A null result is the optimistic-concurrency guard tripping, not a failure to act: the
		// live pending count moved between preview and confirm, so nothing was deleted. 409 tells
		// the client to re-read rather than retry blindly.
		if (result is null)
		{
			return TypedResults.Conflict(PendingCountChanged);
		}

		return TypedResults.Ok(new RequeuePendingResponse
		{
			DeletedDescriptionCount = result.DeletedDescriptionCount,
			UnlinkedItemCount = result.UnlinkedItemCount,
			ClearedMatchScoreCount = result.ClearedMatchScoreCount,
		});
	}

	private static NormalizedDescriptionResponse ToResponse(NormalizedDescriptionDetail detail)
	{
		NormalizedDescription source = detail.Description;
		return new NormalizedDescriptionResponse
		{
			Id = source.Id,
			CanonicalName = source.CanonicalName,
			Status = source.Status switch
			{
				DomainStatus.Active => DtoStatus.Active,
				DomainStatus.PendingReview => DtoStatus.PendingReview,
				_ => throw new InvalidOperationException($"Unhandled status value: {source.Status}"),
			},
			CreatedAt = source.CreatedAt,
			LinkedItemCount = detail.LinkedItemCount,
			SampleRawDescriptions = [.. detail.SampleRawDescriptions],
			// Both neighbour fields stay null together when nothing was recorded — the contract
			// says clients must read that as "no comparison recorded" rather than a zero score
			// (RECEIPTS-873).
			NearestNeighbourName = detail.NearestNeighbourName,
			NearestNeighbourSimilarity = source.NearestNeighbourSimilarity,
		};
	}

	private static NormalizedDescriptionSettingsResponse ToResponse(NormalizedDescriptionSettings settings) => new()
	{
		Id = settings.Id,
		AutoAcceptThreshold = settings.AutoAcceptThreshold,
		PendingReviewThreshold = settings.PendingReviewThreshold,
		UpdatedAt = settings.UpdatedAt,
	};

	private static MatchTestResultResponse ToResponse(MatchTestResult result) => new()
	{
		SimulatedOutcome = result.SimulatedOutcome,
		SimulatedTargetId = result.SimulatedTargetId,
		Candidates = [.. result.Candidates.Select(c => new MatchCandidateDto
		{
			NormalizedDescriptionId = c.NormalizedDescriptionId,
			CanonicalName = c.CanonicalName,
			CosineSimilarity = c.CosineSimilarity,
			Status = c.Status,
		})],
	};

	private static ThresholdImpactPreviewResponse ToResponse(ThresholdImpactPreview preview) => new()
	{
		Current = ToResponse(preview.Current),
		Proposed = ToResponse(preview.Proposed),
		Deltas = new ReclassificationDeltasDto
		{
			AutoToPending = preview.Deltas.AutoToPending,
			PendingToAuto = preview.Deltas.PendingToAuto,
			UnresolvedToAuto = preview.Deltas.UnresolvedToAuto,
			UnresolvedToPending = preview.Deltas.UnresolvedToPending,
		},
	};

	private static ClassificationCountsDto ToResponse(ClassificationCounts counts) => new()
	{
		AutoAccepted = counts.AutoAccepted,
		PendingReview = counts.PendingReview,
		Unresolved = counts.Unresolved,
	};
}
