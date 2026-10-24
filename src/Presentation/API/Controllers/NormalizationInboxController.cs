using System.Security.Claims;
using API.Generated.Dtos;
using API.Http;
using Application.Normalization;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/normalization-inbox")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), 400, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), 403, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), 404, "application/problem+json")]
[ProducesResponseType(typeof(ProblemDetails), 409, "application/problem+json")]
[Authorize(Policy = "RequireAdmin")]
public class NormalizationInboxController(INormalizationReviewInboxService service) : ControllerBase
{
	private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
	[HttpGet]
	[EndpointSummary("List saved purchases still needing review")]
	public Task<Results<Ok<NormalizationReviewInboxPageResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> List(
		string? q = null, int offset = 0, int limit = 25, CancellationToken cancellationToken = default) => Execute(async () =>
	{
		var page = await service.ListAsync(Actor, q, offset, limit, cancellationToken);
		return new NormalizationReviewInboxPageResponse
		{
			Total = page.Total,
			Offset = page.Offset,
			Limit = page.Limit,
			Items = page.Items.Select(value => new NormalizationReviewInboxSourceResponse
			{
				SourceHash = value.SourceHash,
				Source = new() { Location = value.Source.Location, Description = value.Source.Description, ItemCode = value.Source.ItemCode },
				PurchaseCount = value.PurchaseCount,
				ReceiptCount = value.ReceiptCount,
				FirstSeen = value.FirstSeen,
				Receipts = value.Receipts.Select(receipt => new NormalizationReviewReceiptResponse { ReceiptId = receipt.ReceiptId, ItemId = receipt.ItemId, Date = receipt.Date }).ToList(),
				AssistanceState = value.AssistanceState,
				AttemptRevision = value.AttemptRevision,
				RunId = value.RunId,
				ErrorCode = value.ErrorCode,
				ErrorDetail = value.ErrorDetail,
			}).ToList(),
		};
	});
	[HttpGet("preferences")]
	[EndpointSummary("Read post-save assistance consent")]
	public Task<Results<Ok<NormalizationAssistancePreferencesResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> GetPreferences(CancellationToken cancellationToken)
		=> Execute(async () => Map(await service.GetPreferencesAsync(Actor, cancellationToken)));
	[HttpPut("preferences")]
	[EndpointSummary("Configure optional post-save suggestions")]
	public Task<Results<Ok<NormalizationAssistancePreferencesResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> UpdatePreferences(
		[FromBody] NormalizationAssistancePreferenceRequest request, CancellationToken cancellationToken)
		=> Execute(async () => Map(await service.UpdatePreferencesAsync(Actor, new(request.Enabled, request.Model, request.DailyLimit, request.ExpectedRevision), cancellationToken)));
	[HttpPost("sources/{sourceHash}/retry-admission")]
	[EndpointSummary("Retry a request that never started inference")]
	public Task<Results<Ok<NormalizationAssistancePreferencesResponse>, BadRequest<ProblemDetails>, ForbiddenProblem, NotFound<ProblemDetails>, Conflict<ProblemDetails>>> RetryAdmission(
		string sourceHash, [FromBody] NormalizationAdmissionRetryRequest request, CancellationToken cancellationToken) => Execute(async () =>
	{
		await service.RetryAdmissionAsync(Actor, sourceHash, request.ExpectedRevision, cancellationToken);
		return Map(await service.GetPreferencesAsync(Actor, cancellationToken));
	});
	private static NormalizationAssistancePreferencesResponse Map(NormalizationAssistancePreferences value)
		=> new() { IsOwner = value.IsOwner, Enabled = value.Enabled, Model = value.Model, DailyLimit = value.DailyLimit, Revision = value.Revision };
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
				_ => ApiProblem.Conflict(exception.Message, new Dictionary<string, object?> { ["code"] = exception.Code }),
			};
		}
	}
}
