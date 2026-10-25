using System.Security.Claims;
using API.Generated.Dtos;
using API.Http;
using Application.Taxonomy;
using Asp.Versioning;
using Domain.Taxonomy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/taxonomy")]
[Authorize]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class TaxonomyController(ITaxonomyService service) : ControllerBase
{
	[HttpGet]
	[EndpointSummary("Get the complete current taxonomy")]
	public Task<Results<Ok<TaxonomyRevisionResponse>, NotFoundProblem>> GetCurrent(CancellationToken cancellationToken) => Read(null, cancellationToken);

	[HttpGet("revisions/{id:guid}")]
	[EndpointSummary("Get an immutable taxonomy revision")]
	public Task<Results<Ok<TaxonomyRevisionResponse>, NotFoundProblem>> GetRevision(Guid id, CancellationToken cancellationToken) => Read(id, cancellationToken);

	private async Task<Results<Ok<TaxonomyRevisionResponse>, NotFoundProblem>> Read(Guid? id, CancellationToken ct)
	{
		TaxonomyRevision? revision = await service.ReadAsync(id, ct);
		if (revision == null) { return ApiProblem.NotFound("Taxonomy revision does not exist."); }
		return TypedResults.Ok(Map(revision));
	}

	[HttpPost("changes")]
	[Authorize(Policy = "RequireAdmin")]
	[EndpointSummary("Apply an atomic taxonomy change batch")]
	public async Task<Results<Ok<TaxonomyRevisionResponse>, BadRequestProblem, ForbiddenProblem, NotFoundProblem, ConflictProblem>> Change(
		[FromBody] TaxonomyChangeRequest request, CancellationToken cancellationToken)
	{
		if (request.Changes == null || request.Changes.Any(change => change == null))
		{ return ApiProblem.BadRequest("Changes must contain non-null taxonomy changes."); }
		try
		{
			TaxonomyRevision revision = await service.ChangeAsync(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
				new(request.ExpectedRevisionId, request.OperationId, request.Reason, request.Changes.Select(change =>
					new TaxonomyEdit(change.Kind.ToString().ToLowerInvariant(), change.CategoryId, change.ParentId, change.Name, change.Definition)).ToArray()), cancellationToken);
			return TypedResults.Ok(Map(revision));
		}
		catch (TaxonomyException error)
		{
			return error.Status switch
			{
				403 => new ForbiddenProblem(error.Message, "admin_required"),
				409 => ApiProblem.Conflict(error.Message),
				_ => ApiProblem.BadRequest(error.Message)
			};
		}
	}

	private static TaxonomyRevisionResponse Map(TaxonomyRevision revision) => new()
	{
		Id = revision.Id,
		Sequence = revision.Sequence,
		PreviousRevisionId = revision.PreviousRevisionId,
		RecordedAt = revision.RecordedAt,
		Actor = revision.Actor,
		Reason = revision.Reason,
		OperationId = revision.OperationId,
		AffectedCount = revision.AffectedCount,
		Nodes = revision.Nodes.Select(node => new TaxonomyNodeResponse
		{ Id = node.Id, ParentId = node.ParentId, Name = node.Name, Definition = node.Definition, Archived = node.Archived }).ToList(),
	};
}
