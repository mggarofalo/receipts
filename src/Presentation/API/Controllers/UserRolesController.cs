using API.Authentication;
using API.Generated.Dtos;
using Application.Interfaces.Services;
using Application.Models;
using Asp.Versioning;
using Infrastructure.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/users/{userId}/roles")]
[Produces("application/json")]
[Authorize(Policy = "RequireAdmin")]
public class UserRolesController(
	UserManager<ApplicationUser> userManager,
	IRoleManagementService roleManagementService) : ControllerBase
{
	[HttpGet]
	[EndpointSummary("List roles for a user")]
	public async Task<Results<Ok<UserRolesResponse>, NotFound>> GetUserRoles([FromRoute] string userId)
	{
		ApplicationUser? user = await userManager.FindByIdAsync(userId);
		if (user is null)
		{
			return TypedResults.NotFound();
		}

		IList<string> roles = await userManager.GetRolesAsync(user);
		return TypedResults.Ok(new UserRolesResponse
		{
			Roles = [.. roles],
		});
	}

	[HttpPost("{role}")]
	[EndpointSummary("Assign role to user")]
	public async Task<Results<NoContent, NotFound, BadRequest<ProblemDetails>, Conflict<ProblemDetails>>> AssignUserRole([FromRoute] string userId, [FromRoute] string role)
	{
		string? actorId = RoleChangeActor.GetSubject(User);
		if (actorId is null)
		{
			return ApiProblem.BadRequest(RoleChangeActor.InvalidCredentials);
		}

		RoleChangeResult result = await roleManagementService.ChangeAsync(
			userId, actorId,
			RoleChangeMode.Add, [role], cancellationToken: HttpContext.RequestAborted);
		return RoleChangeResponse.From(result);
	}

	[HttpDelete("{role}")]
	[EndpointSummary("Remove role from user")]
	public async Task<Results<NoContent, NotFound, BadRequest<ProblemDetails>, Conflict<ProblemDetails>>> RemoveUserRole([FromRoute] string userId, [FromRoute] string role)
	{
		string? actorId = RoleChangeActor.GetSubject(User);
		if (actorId is null)
		{
			return ApiProblem.BadRequest(RoleChangeActor.InvalidCredentials);
		}

		RoleChangeResult result = await roleManagementService.ChangeAsync(
			userId, actorId,
			RoleChangeMode.Remove, [role], cancellationToken: HttpContext.RequestAborted);
		return RoleChangeResponse.From(result);
	}
}
