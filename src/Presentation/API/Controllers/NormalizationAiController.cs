using System.Security.Claims;
using API.Generated.Dtos;
using API.Http;
using Application.NormalizationAi;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/normalization-ai")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
[Authorize(Policy = "RequireAdmin")]
public class NormalizationAiController(INormalizationAiService service) : ControllerBase
{
	private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

	[HttpGet("connection")]
	[EndpointSummary("Get ChatGPT credential and model status")]
	public Task<Results<Ok<NormalizationAiConnectionResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>, ServiceUnavailableProblem>> GetConnection(CancellationToken cancellationToken) =>
		Execute(async () => { NormalizationAiConnection value = await service.GetConnectionAsync(Actor, cancellationToken); return new NormalizationAiConnectionResponse { Busy = value.Busy, Configured = value.Configured, IsOwner = value.IsOwner, CredentialsPresent = value.CredentialsPresent, Generation = value.Generation, Models = value.Models.ToList() }; });

	[HttpGet("candidates")]
	[EndpointSummary("List unresolved items for explicit selection")]
	public Task<Results<Ok<NormalizationAiCandidatePageResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>, ServiceUnavailableProblem>> ListCandidates(string? q = null, int offset = 0, int limit = 25, CancellationToken cancellationToken = default) =>
		Execute(async () => { NormalizationAiCandidatePage value = await service.ListCandidatesAsync(Actor, q, offset, limit, cancellationToken); return new NormalizationAiCandidatePageResponse { Total = value.Total, Items = value.Items.Select(item => new NormalizationAiCandidateResponse { Id = item.Id, ReceiptId = item.ReceiptId, Description = item.Description, Location = item.Location, Date = item.Date }).ToList() }; });

	[HttpGet("runs")]
	[EndpointSummary("List recent normalization runs")]
	public Task<Results<Ok<List<NormalizationAiRunResponse>>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>, ServiceUnavailableProblem>> ListRuns(CancellationToken cancellationToken) =>
		Execute(async () => (await service.ListRunsAsync(Actor, cancellationToken)).Select(Map).ToList());

	[HttpGet("runs/{id:guid}")]
	[EndpointSummary("Get a resumable normalization run")]
	public Task<Results<Ok<NormalizationAiRunResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>, ServiceUnavailableProblem>> GetRun(Guid id, CancellationToken cancellationToken) =>
		Execute(async () => Map(await service.GetRunAsync(Actor, id, cancellationToken) ?? throw new NormalizationAiException(404, "not_found", "Normalization run not found.")));

	[HttpPost("runs")]
	[EndpointSummary("Request suggestions for selected items")]
	public Task<Results<Ok<NormalizationAiRunResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>, ServiceUnavailableProblem>> Start([FromBody] StartNormalizationAiRunRequest request, CancellationToken cancellationToken) =>
		Execute(async () => Map(await service.StartAsync(Actor, request.ClientRequestId, request.ItemIds.ToList(), request.Model, cancellationToken)));

	[HttpPost("runs/{id:guid}/cancel")]
	[EndpointSummary("Cancel a normalization run")]
	public Task<Results<Ok<NormalizationAiRunResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>, ServiceUnavailableProblem>> Cancel(Guid id, CancellationToken cancellationToken) =>
		Execute(async () => Map(await service.CancelAsync(Actor, id, cancellationToken)));

	[HttpPost("runs/{id:guid}/retry")]
	[EndpointSummary("Explicitly retry a normalization run")]
	public Task<Results<Ok<NormalizationAiRunResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>, ServiceUnavailableProblem>> Retry(Guid id, [FromBody] RetryNormalizationAiRunRequest request, CancellationToken cancellationToken) =>
		Execute(async () => Map(await service.RetryAsync(Actor, id, request.ClientRequestId, cancellationToken)));

	[HttpPost("runs/{id:guid}/apply")]
	[EndpointSummary("Atomically apply reviewed suggestions")]
	public Task<Results<Ok<NormalizationAiRunResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>, ServiceUnavailableProblem>> Apply(Guid id, [FromBody] ApplyNormalizationAiRunRequest request, CancellationToken cancellationToken) =>
		Execute(async () => Map(await service.ApplyAsync(Actor, id, request.ProposalIds.ToList(), request.ExpectedRevision, cancellationToken)));

	private async Task<Results<Ok<T>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>, ServiceUnavailableProblem>> Execute<T>(Func<Task<T>> action)
	{
		try { return TypedResults.Ok(await action()); }
		catch (NormalizationAiException exception)
		{
			// Keep status-specific result types for schema generation, while using
			// RFC 9457's media type (the built-in typed JSON results use application/json).
			Response.OnStarting(() =>
			{
				Response.ContentType = "application/problem+json";
				return Task.CompletedTask;
			});
			return exception.StatusCode switch
			{
				400 => ApiProblem.BadRequest(exception.Message),
				403 => new ForbiddenProblem(exception.Message, exception.Code),
				404 => ApiProblem.NotFound(exception.Message),
				503 => new ServiceUnavailableProblem(exception.Message, exception.Code),
				_ => ApiProblem.Conflict(exception.Message, new Dictionary<string, object?> { ["code"] = exception.Code }),
			};
		}
		catch (ChatGptRunnerException exception)
		{
			return new ServiceUnavailableProblem("ChatGPT is unavailable. Check its connection on the server and try again.", exception.Code);
		}
	}

	private static NormalizationAiRunResponse Map(NormalizationAiRun value) => new()
	{
		Id = value.Id,
		Revision = value.Revision,
		ClientRequestId = value.ClientRequestId,
		State = value.State,
		Model = value.Model,
		CreatedAt = value.CreatedAt,
		CompletedAt = value.CompletedAt,
		ErrorCode = value.ErrorCode,
		ErrorDetail = value.ErrorDetail,
		Usage = value.Usage is null ? null : new NormalizationAiUsageResponse { Input = value.Usage.Input, Output = value.Usage.Output, CacheRead = value.Usage.CacheRead, CacheWrite = value.Usage.CacheWrite },
		Proposals = value.Proposals.Select(proposal => new NormalizationAiProposalResponse
		{
			Id = proposal.Id,
			ReceiptItemId = proposal.ReceiptItemId,
			Description = proposal.Description,
			Action = proposal.Action,
			TargetId = proposal.TargetId,
			CanonicalName = proposal.CanonicalName,
			DisplayLabel = proposal.DisplayLabel,
			Reason = proposal.Reason,
			State = proposal.State,
		}).ToList(),
	};
}
