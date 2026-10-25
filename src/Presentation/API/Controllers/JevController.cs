using API.Generated.Dtos;
using Application.Classification;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/jev")]
[Authorize(Policy = "RequireAdmin")]
public sealed class JevController(IClassificationProviderConnection connection) : ControllerBase
{
	[HttpGet("status")]
	[ProducesResponseType(StatusCodes.Status401Unauthorized)]
	[ProducesResponseType(StatusCodes.Status403Forbidden)]
	[EndpointSummary("Get passive Jev configuration status")]
	public Ok<JevConnectionResponse> Status() => TypedResults.Ok(Map(connection.GetStatus()));
	[HttpPost("check")]
	[ProducesResponseType(StatusCodes.Status401Unauthorized)]
	[ProducesResponseType(StatusCodes.Status403Forbidden)]
	[EndpointSummary("Check Jev authentication and list models without inference")]
	public async Task<Ok<JevConnectionResponse>> Check(CancellationToken cancellationToken) => TypedResults.Ok(Map(await connection.CheckAsync(cancellationToken)));
	private static JevConnectionResponse Map(ProviderConnectionStatus status) => new()
	{
		State = Enum.Parse<JevConnectionState>(status.State.ToString(), true),
		Reason = status.Reason,
		Models = status.Models.ToList()
	};
}
