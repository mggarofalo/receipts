using System.Security.Claims;
using API.Generated.Dtos;
using API.Http;
using Application.Normalization;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SourceKey = Application.Normalization.NormalizationSourceKey;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/normalization-catalog")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
[Authorize(Policy = "RequireAdmin")]
public class NormalizationCatalogController(INormalizationCatalogService service) : ControllerBase
{
	private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

	[HttpPost("targets/{id:guid}/preview-label")]
	[EndpointSummary("Preview a global display label change and affected purchases")]
	public Task<Results<Ok<NormalizationCatalogLabelReviewResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> PreviewLabel(
		Guid id, [FromBody] NormalizationCatalogLabelPreviewRequest request, CancellationToken cancellationToken) =>
		Execute(async () => Map(await service.PreviewLabelAsync(Actor, id, request.DisplayLabel, cancellationToken)));

	[HttpPost("targets/{id:guid}/approve-label")]
	[EndpointSummary("Approve a global display label change at the reviewed revision")]
	public Task<Results<Ok<NormalizationCatalogLabelReviewResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> ApproveLabel(
		Guid id, [FromBody] NormalizationCatalogLabelApprovalRequest request, CancellationToken cancellationToken) =>
		Execute(async () => Map(await service.ApproveLabelAsync(Actor, id,
			new(request.DisplayLabel, request.ExpectedTargetRevision, request.ExpectedAffectedItemCount,
				request.SuggestionRunId, request.SuggestionProposalId, request.SuggestionRunRevision), cancellationToken)));

	private static NormalizationCatalogLabelReviewResponse Map(NormalizationCatalogLabelReview value) => new()
	{
		TargetId = value.TargetId,
		TargetRevision = value.TargetRevision,
		CanonicalName = value.CanonicalName,
		CurrentDisplayLabel = value.CurrentDisplayLabel,
		ProposedDisplayLabel = value.ProposedDisplayLabel,
		AffectedItemCount = value.AffectedItemCount,
	};

	[HttpGet("sources")]
	[EndpointSummary("List distinct receipt descriptions and their normalization coverage")]
	public Task<Results<Ok<NormalizationSourcePageResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> ListSources(
		string? q = null, string disposition = "needs-review", int offset = 0, int limit = 25, CancellationToken cancellationToken = default) =>
		Execute(async () =>
		{
			NormalizationSourcePage page = await service.ListSourcesAsync(Actor, q, disposition, offset, limit, cancellationToken);
			return new NormalizationSourcePageResponse
			{
				Total = page.Total,
				Items = page.Items.Select(group => new NormalizationSourceGroupResponse
				{
					Source = Map(group.Source),
					ItemCount = group.ItemCount,
					UnassignedCount = group.UnassignedCount,
					LegacyUnknownCount = group.LegacyUnknownCount,
					AutomaticCount = group.AutomaticCount,
					ProtectedCount = group.ProtectedCount,
					MappingId = group.MappingId,
					RevisableCount = group.RevisableCount,
					LinkedTargetCount = group.LinkedTargetCount,
					LinkedTargets = (group.LinkedTargets ?? []).Select(target => new NormalizationLinkedTargetResponse
					{
						Id = target.Id,
						Name = target.Name,
						Status = target.Status switch
						{
							Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active => API.Generated.Dtos.NormalizedDescriptionStatus.Active,
							Domain.NormalizedDescriptions.NormalizedDescriptionStatus.PendingReview => API.Generated.Dtos.NormalizedDescriptionStatus.PendingReview,
							_ => API.Generated.Dtos.NormalizedDescriptionStatus.Rejected,
						},
					}).ToList(),
				}).ToList(),
			};
		});

	[HttpGet("coverage")]
	[EndpointSummary("Get normalization coverage across live receipt items")]
	public Task<Results<Ok<NormalizationCatalogCoverageResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> GetCoverage(CancellationToken cancellationToken) =>
		Execute(async () =>
		{
			NormalizationCatalogCoverage value = await service.GetCoverageAsync(Actor, cancellationToken);
			return new NormalizationCatalogCoverageResponse
			{
				TotalItems = value.TotalItems,
				Unassigned = value.Unassigned,
				LegacyUnknown = value.LegacyUnknown,
				Automatic = value.Automatic,
				Reviewed = value.Reviewed,
				Mapped = value.Mapped,
				Protected = value.Protected,
				Excluded = value.Excluded,
				Revisable = value.Revisable,
			};
		});

	[HttpGet("candidates")]
	[EndpointSummary("Get matching candidates and approved source examples")]
	public Task<Results<Ok<List<NormalizationCatalogCandidateResponse>>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> GetCandidates(
		string location, string description, string? itemCode = null, CancellationToken cancellationToken = default) =>
		Execute(async () => (await service.GetCandidatesAsync(Actor, new(location, description, itemCode), cancellationToken))
			.Select(candidate => new NormalizationCatalogCandidateResponse
			{
				Id = candidate.Id,
				Revision = candidate.Revision,
				CanonicalName = candidate.CanonicalName,
				DisplayLabel = candidate.DisplayLabel,
				Channels = candidate.Channels.ToList(),
				ApprovedExamples = candidate.ApprovedExamples.ToList(),
			}).ToList());

	[HttpGet("mappings")]
	[EndpointSummary("Get mapping history for a scoped source description")]
	public Task<Results<Ok<List<NormalizationSourceMappingResponse>>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> ListMappings(
		string location, string description, string? itemCode = null, CancellationToken cancellationToken = default) =>
		Execute(async () => (await service.ListMappingsAsync(Actor, new(location, description, itemCode), cancellationToken)).Select(Map).ToList());

	[HttpPost("operations")]
	[EndpointSummary("Preview a scoped mapping and historical changes without applying them")]
	public Task<Results<Ok<NormalizationCatalogOperationResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> Preview(
		[FromBody] NormalizationCatalogPreviewRequest request, CancellationToken cancellationToken) =>
		Execute(async () => Map(await service.PreviewAsync(Actor,
			new(request.ClientRequestId, Map(request.Source), request.TargetId, request.TargetRevision,
								request.NewCanonicalName, request.IncludeLegacyUnknown, request.Exclude,
								request.SuggestionRunId, request.SuggestionProposalId, request.SuggestionRunRevision), cancellationToken)));

	[HttpGet("operations")]
	[EndpointSummary("List recent historical normalization operations")]
	public Task<Results<Ok<List<NormalizationCatalogOperationResponse>>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> ListOperations(CancellationToken cancellationToken) =>
		Execute(async () => (await service.ListOperationsAsync(Actor, cancellationToken)).Select(Map).ToList());

	[HttpGet("operations/{id:guid}")]
	[EndpointSummary("Get historical normalization progress")]
	public Task<Results<Ok<NormalizationCatalogOperationResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> GetOperation(Guid id, CancellationToken cancellationToken) =>
		Execute(async () => Map(await service.GetOperationAsync(Actor, id, cancellationToken)
			?? throw new NormalizationCatalogException(404, "not_found", "Normalization operation not found.")));

	[HttpPost("operations/{id:guid}/approve")]
	[EndpointSummary("Approve the preview and apply a bounded historical batch")]
	public Task<Results<Ok<NormalizationCatalogOperationResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> Approve(
		Guid id, [FromBody] NormalizationCatalogRevisionRequest request, CancellationToken cancellationToken) =>
		Execute(async () => Map(await service.ApproveAsync(Actor, id, request.ExpectedRevision, cancellationToken)));

	[HttpPost("operations/{id:guid}/resume")]
	[EndpointSummary("Continue an approved historical operation")]
	public Task<Results<Ok<NormalizationCatalogOperationResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> Resume(Guid id, CancellationToken cancellationToken) =>
		Execute(async () => Map(await service.ResumeAsync(Actor, id, cancellationToken)));

	[HttpPost("operations/{id:guid}/cancel")]
	[EndpointSummary("Stop propagation and revoke the operation mapping")]
	public Task<Results<Ok<NormalizationCatalogOperationResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> Cancel(
		Guid id, [FromBody] NormalizationCatalogRevisionRequest request, CancellationToken cancellationToken) =>
		Execute(async () => Map(await service.CancelAsync(Actor, id, request.ExpectedRevision, cancellationToken)));

	[HttpPost("operations/{id:guid}/undo")]
	[EndpointSummary("Undo changes still owned by this operation")]
	public Task<Results<Ok<NormalizationCatalogOperationResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> Undo(
		Guid id, [FromBody] NormalizationCatalogRevisionRequest request, CancellationToken cancellationToken) =>
		Execute(async () => Map(await service.UndoAsync(Actor, id, request.ExpectedRevision, cancellationToken)));

	[HttpPost("mappings/{id:guid}/revoke")]
	[EndpointSummary("Revoke a source mapping to stop future reuse")]
	public Task<Results<Ok<NormalizationSourceMappingResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> Revoke(
		Guid id, [FromBody] NormalizationCatalogRevisionRequest request, CancellationToken cancellationToken) =>
		Execute(async () => Map(await service.RevokeAsync(Actor, id, request.ExpectedRevision, cancellationToken)));

	private async Task<Results<Ok<T>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> Execute<T>(Func<Task<T>> action)
	{
		try { return TypedResults.Ok(await action()); }
		catch (NormalizationCatalogException exception)
		{
			Response.OnStarting(() => { Response.ContentType = "application/problem+json"; return Task.CompletedTask; });
			return exception.StatusCode switch
			{
				400 => ApiProblem.BadRequest(exception.Message),
				403 => new ForbiddenProblem(exception.Message, exception.Code),
				404 => ApiProblem.NotFound(exception.Message),
				_ => ApiProblem.Conflict(exception.Message, new Dictionary<string, object?>(exception.Extensions) { ["code"] = exception.Code }),
			};
		}
	}

	private static SourceKey Map(API.Generated.Dtos.NormalizationSourceKey value) => new(value.Location, value.Description, value.ItemCode);
	private static API.Generated.Dtos.NormalizationSourceKey Map(SourceKey value) => new() { Location = value.Location, Description = value.Description, ItemCode = value.ItemCode };
	private static NormalizationSourceMappingResponse Map(NormalizationSourceMapping value) => new()
	{
		Id = value.Id,
		Revision = value.Revision,
		Source = Map(value.Source),
		TargetId = value.TargetId,
		TargetName = value.TargetName,
		State = value.State,
		ApprovedAt = value.ApprovedAt,
	};
	private static NormalizationCatalogOperationResponse Map(NormalizationCatalogOperation value) => new()
	{
		Id = value.Id,
		Revision = value.Revision,
		State = value.State,
		Source = Map(value.Source),
		MappingId = value.MappingId,
		TargetId = value.TargetId,
		NewCanonicalName = value.NewCanonicalName,
		IncludeLegacyUnknown = value.IncludeLegacyUnknown,
		Total = value.Total,
		Eligible = value.Eligible,
		Protected = value.Protected,
		Applied = value.Applied,
		Conflicted = value.Conflicted,
		Remaining = value.Remaining,
		Undone = value.Undone,
		CreatedAt = value.CreatedAt,
		TargetName = value.TargetName,
		Exclude = value.Exclude,
		ErrorCode = value.ErrorCode,
		ErrorDetail = value.ErrorDetail,
	};
}
