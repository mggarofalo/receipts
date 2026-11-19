using System.Security.Claims;
using API.Generated.Dtos;
using API.Mapping.Core;
using Application.Commands.Ynab.Connection;
using Application.Models.Ynab;
using Application.Queries.Core.Ynab;
using Asp.Versioning;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

/// <summary>
/// Connects and disconnects the deployment's YNAB OAuth connection. See docs/ynab-connection.md.
/// Nothing here returns or logs a token, an authorization code or the client secret.
/// </summary>
[ApiVersion("1.0")]
[ApiController]
[Route("api/ynab/oauth")]
[Authorize(Policy = "RequireAdmin")]
public class YnabOAuthController(IMediator mediator, YnabMapper mapper) : ControllerBase
{
	[HttpPost("authorization")]
	[ProducesResponseType(StatusCodes.Status403Forbidden)]
	[EndpointSummary("Start a YNAB OAuth authorization")]
	[EndpointDescription("Creates a single-use authorization attempt, valid for ten minutes and bound to the calling Admin, and returns the YNAB address the browser must visit. Admin-only.")]
	public async Task<Results<Ok<YnabAuthorizationStartResponse>, ConflictProblem, ForbidHttpResult>> StartAuthorization(CancellationToken cancellationToken)
	{
		string? userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
		if (string.IsNullOrEmpty(userId))
		{
			return TypedResults.Forbid();
		}

		string? authorizationUrl = await mediator.Send(new StartYnabAuthorizationCommand(userId), cancellationToken);
		if (authorizationUrl is null)
		{
			return ApiProblem.Conflict("YNAB OAuth is not configured on this deployment");
		}

		return TypedResults.Ok(new YnabAuthorizationStartResponse { AuthorizationUrl = new Uri(authorizationUrl) });
	}

	[HttpPost("callback")]
	[ProducesResponseType(StatusCodes.Status403Forbidden)]
	[EndpointSummary("Complete a YNAB OAuth authorization")]
	[EndpointDescription("Exchanges the code YNAB returned for tokens and stores them as the deployment's YNAB connection. Must be called by the Admin who started the attempt. Admin-only.")]
	[ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
	public async Task<Results<Ok<YnabConnectionStatusResponse>, BadRequestProblem, ForbidHttpResult, StatusCodeHttpResult>> CompleteAuthorization(
		[FromBody] CompleteYnabAuthorizationRequest request,
		CancellationToken cancellationToken)
	{
		string? userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
		if (string.IsNullOrEmpty(userId))
		{
			return TypedResults.Forbid();
		}

		YnabAuthorizationCompletion completion = await mediator.Send(
			new CompleteYnabAuthorizationCommand(userId, request.Code, request.State), cancellationToken);

		switch (completion)
		{
			case YnabAuthorizationCompletion.AttemptNotValid:
				return ApiProblem.BadRequest("The YNAB authorization attempt is not valid. It may have expired or already been used; start again.");
			case YnabAuthorizationCompletion.RejectedByYnab:
				return ApiProblem.BadRequest("YNAB rejected the authorization. Start again.");
			case YnabAuthorizationCompletion.YnabUnavailable:
				return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
		}

		YnabConnectionStatus status = await mediator.Send(new GetYnabConnectionStatusQuery(), cancellationToken);
		return TypedResults.Ok(mapper.ToConnectionStatusResponse(status));
	}

	[HttpDelete("connection")]
	[ProducesResponseType(StatusCodes.Status403Forbidden)]
	[EndpointSummary("Disconnect the YNAB OAuth connection")]
	[EndpointDescription("Asks YNAB to revoke the grant, then removes the stored tokens whether or not YNAB could be reached. Admin-only.")]
	public async Task<Ok<YnabDisconnectResponse>> Disconnect(CancellationToken cancellationToken)
	{
		Application.Models.Ynab.YnabRemoteRevocation revocation = await mediator.Send(new DisconnectYnabCommand(), cancellationToken);
		return TypedResults.Ok(mapper.ToDisconnectResponse(revocation));
	}
}
