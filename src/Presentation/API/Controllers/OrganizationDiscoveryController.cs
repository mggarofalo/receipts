using Application.Classification;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

public sealed record OrganizationDiscoveryStatusResponse(OrganizationDiscoveryProgress Discovery,
	string Capability, bool AutomaticClassificationAvailable);

[ApiVersion("1.0")]
[ApiController]
[Route("api/category-assistance/discovery")]
[Authorize]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class OrganizationDiscoveryController(IOrganizationDiscoveryReader reader,
	ICategoryAssistanceCapabilitySource capabilitySource) : ControllerBase
{
	[HttpGet]
	[EndpointSummary("Read aggregate background discovery progress")]
	public async Task<Ok<OrganizationDiscoveryStatusResponse>> Status(CancellationToken ct)
	{
		var progress = await reader.GetProgressAsync(ct);
		var capability = await capabilitySource.GetAsync(ct);
		return TypedResults.Ok(new OrganizationDiscoveryStatusResponse(progress,
			capability.State.ToString().ToLowerInvariant(), false));
	}
}
