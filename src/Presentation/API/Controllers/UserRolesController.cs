using API.Generated.Dtos;
using Asp.Versioning;
using Common;
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
	UserManager<ApplicationUser> userManager) : ControllerBase
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
	public async Task<Results<NoContent, BadRequest<ProblemDetails>, NotFound>> AssignUserRole([FromRoute] string userId, [FromRoute] string role)
	{
		if (!AppRoles.All.Contains(role))
		{
			return ApiProblem.BadRequest($"Invalid role '{role}'. Valid roles: {string.Join(", ", AppRoles.All)}");
		}

		ApplicationUser? user = await userManager.FindByIdAsync(userId);
		if (user is null)
		{
			return TypedResults.NotFound();
		}

		await userManager.AddToRoleAsync(user, role);
		return TypedResults.NoContent();
	}

	[HttpDelete("{role}")]
	[EndpointSummary("Remove role from user")]
	public async Task<Results<NoContent, BadRequest<ProblemDetails>, NotFound>> RemoveUserRole([FromRoute] string userId, [FromRoute] string role)
	{
		if (!AppRoles.All.Contains(role))
		{
			return ApiProblem.BadRequest($"Invalid role '{role}'. Valid roles: {string.Join(", ", AppRoles.All)}");
		}

		ApplicationUser? user = await userManager.FindByIdAsync(userId);
		if (user is null)
		{
			return TypedResults.NotFound();
		}

		await userManager.RemoveFromRoleAsync(user, role);
		return TypedResults.NoContent();
	}
}
