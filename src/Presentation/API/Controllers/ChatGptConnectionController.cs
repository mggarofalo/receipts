using System.Security.Claims;
using API.Generated.Dtos;
using API.Http;
using Application.NormalizationAi;
using Asp.Versioning;
using Infrastructure.NormalizationAi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/normalization-ai/connection")]
[Authorize(Policy = "RequireAdmin")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ChatGptConnectionController(IChatGptDeviceSignIn signIn, IOptions<NormalizationAiOptions> options) : ControllerBase
{
	private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

	[HttpPost("sign-in")]
	[EndpointSummary("Start explicit owner sign-in")]
	public Task<Results<Ok<ChatGptSignInResponse>, BadRequestProblem, ForbiddenProblem, ConflictProblem, ServiceUnavailableProblem>> Start([FromBody] ChatGptSignInRequest request) =>
		Execute(() => Task.FromResult(Map(signIn.Start(Actor, request.OwnerConsent))));

	[HttpPost("reconnect")]
	[EndpointSummary("Start fresh explicit owner sign-in")]
	public Task<Results<Ok<ChatGptSignInResponse>, BadRequestProblem, ForbiddenProblem, ConflictProblem, ServiceUnavailableProblem>> Reconnect([FromBody] ChatGptSignInRequest request) => Start(request);

	[HttpGet("sign-in/{id:guid}")]
	[EndpointSummary("Get sign-in attempt")]
	public Task<Results<Ok<ChatGptSignInResponse>, BadRequestProblem, ForbiddenProblem, ConflictProblem, ServiceUnavailableProblem>> Get(Guid id) =>
		Execute(() => Task.FromResult(Map(signIn.Get(Actor, id))));

	[HttpPost("sign-in/{id:guid}/cancel")]
	[EndpointSummary("Cancel sign-in without logout")]
	public Task<Results<Ok<ChatGptSignInResponse>, BadRequestProblem, ForbiddenProblem, ConflictProblem, ServiceUnavailableProblem>> Cancel(Guid id, CancellationToken cancellationToken) =>
		Execute(async () => Map(await signIn.CancelAsync(Actor, id, cancellationToken)));

	[HttpPost("disconnect")]
	[EndpointSummary("Disconnect ChatGPT")]
	public async Task<Results<NoContent, BadRequestProblem, ForbiddenProblem, ConflictProblem, ServiceUnavailableProblem>> Disconnect(CancellationToken cancellationToken)
	{
		var result = await Execute(async () => { await signIn.DisconnectAsync(Actor, cancellationToken); return true; });
		return result.Result switch
		{
			Ok<bool> => TypedResults.NoContent(),
			BadRequestProblem value => value,
			ForbiddenProblem value => value,
			ConflictProblem value => value,
			ServiceUnavailableProblem value => value,
			_ => throw new InvalidOperationException()
		};
	}

	private async Task<Results<Ok<T>, BadRequestProblem, ForbiddenProblem, ConflictProblem, ServiceUnavailableProblem>> Execute<T>(Func<Task<T>> action)
	{
		Response.Headers.CacheControl = "no-store";
		Response.Headers.Pragma = "no-cache";
		Response.Headers["Referrer-Policy"] = "no-referrer";
		// Never reflect provider diagnostics, exception messages or extensions into HTTP.
		if (!options.Value.Enabled || string.IsNullOrWhiteSpace(options.Value.OwnerUserId))
		{
			return new ServiceUnavailableProblem("ChatGPT is not configured for this deployment.", "not_configured");
		}

		if (!string.Equals(Actor, options.Value.OwnerUserId, StringComparison.Ordinal))
		{
			return new ForbiddenProblem("Only the designated ChatGPT connection owner can manage sign-in.", "owner_required");
		}

		try { return TypedResults.Ok(await action()); }
		catch (NormalizationAiException error) when (error.StatusCode == 400)
		{ return ApiProblem.BadRequest("Confirm that you own and authorize use of the ChatGPT plan for this deployment."); }
		catch (NormalizationAiException error) when (error.StatusCode == 403)
		{ return new ForbiddenProblem("Only the designated ChatGPT connection owner can manage sign-in.", "owner_required"); }
		catch (NormalizationAiException error) when (error.StatusCode == 409)
		{ return ApiProblem.Conflict("Another connection operation is in progress."); }
		catch (ChatGptRunnerException error) when (error.Code is "busy" or "operator_busy")
		{ return ApiProblem.Conflict("Another connection operation is in progress."); }
		catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
		catch (Exception)
		{ return new ServiceUnavailableProblem("ChatGPT is unavailable. Check the server connection and try again.", "unavailable"); }
	}

	private static ChatGptSignInResponse Map(ChatGptSignInAttempt value) => new()
	{
		Id = value.Id,
		State = value.State,
		VerificationUrl = value.State == "waiting" ? value.VerificationUrl : null,
		UserCode = value.State == "waiting" ? value.UserCode : null,
		LocalExpiresAt = value.State == "waiting" ? value.ExpiresAt : null
	};
}
