using API.Generated.Dtos;
using API.Mapping.Core;
using Application.Commands.Occasion.Create;
using Application.Commands.Occasion.Delete;
using Application.Commands.Occasion.Update;
using Application.Envelopes;
using Application.Exceptions;
using Application.Queries.Core.Occasion;
using Asp.Versioning;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers.Core;

[ApiVersion("1.0")]
[ApiController]
[Route("api/occasions")]
[Authorize]
public class OccasionsController(IMediator mediator, EnvelopeMapper mapper) : ControllerBase
{
	[HttpGet]
	[EndpointSummary("List occasions")]
	public async Task<Ok<OccasionListResponse>> GetOccasions([FromQuery] bool? isActive = null, CancellationToken cancellationToken = default)
	{
		List<Domain.Envelopes.Occasion> rows = await mediator.Send(new GetAllOccasionsQuery(isActive), cancellationToken);
		return TypedResults.Ok(new OccasionListResponse { Data = rows.Select(mapper.ToResponse).ToList() });
	}

	[HttpGet("{id}")]
	[EndpointSummary("Get an occasion")]
	public async Task<Results<Ok<OccasionResponse>, NotFound>> GetOccasionById([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		Domain.Envelopes.Occasion? row = await mediator.Send(new GetOccasionByIdQuery(id), cancellationToken);
		return row is null ? TypedResults.NotFound() : TypedResults.Ok(mapper.ToResponse(row));
	}

	[HttpPost]
	[EndpointSummary("Create an occasion")]
	public async Task<Results<Created<OccasionResponse>, ConflictProblem>> CreateOccasion([FromBody] CreateOccasionRequest request, CancellationToken cancellationToken = default)
	{
		try
		{
			Domain.Envelopes.Occasion created = await mediator.Send(new CreateOccasionCommand(mapper.ToDomain(request)), cancellationToken);
			return TypedResults.Created($"/api/occasions/{created.Id}", mapper.ToResponse(created));
		}
		catch (DuplicateEntityException ex)
		{
			return ApiProblem.Conflict(ex.Message);
		}
	}

	[HttpPut("{id}")]
	[EndpointSummary("Rename or archive an occasion")]
	public async Task<Results<NoContent, NotFound, ConflictProblem>> UpdateOccasion([FromRoute] Guid id, [FromBody] UpdateOccasionRequest request, CancellationToken cancellationToken = default)
	{
		try
		{
			bool updated = await mediator.Send(new UpdateOccasionCommand(mapper.ToDomain(id, request)), cancellationToken);
			return updated ? TypedResults.NoContent() : TypedResults.NotFound();
		}
		catch (DuplicateEntityException ex)
		{
			return ApiProblem.Conflict(ex.Message);
		}
	}

	[HttpDelete("{id}")]
	[EndpointSummary("Delete an unused occasion")]
	public async Task<Results<NoContent, NotFound, ConflictProblem>> DeleteOccasion([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		try
		{
			bool deleted = await mediator.Send(new DeleteOccasionCommand(id), cancellationToken);
			return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
		}
		catch (EnvelopeException ex) when (ex.Status == StatusCodes.Status409Conflict)
		{
			return ApiProblem.Conflict(ex.Message, ex.Extensions);
		}
	}
}
