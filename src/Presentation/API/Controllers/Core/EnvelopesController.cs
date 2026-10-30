using API.Generated.Dtos;
using API.Mapping.Core;
using Application.Commands.Envelope.Create;
using Application.Commands.Envelope.Delete;
using Application.Commands.Envelope.Update;
using Application.Envelopes;
using Application.Exceptions;
using Application.Queries.Core.Envelope;
using Asp.Versioning;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers.Core;

[ApiVersion("1.0")]
[ApiController]
[Route("api/envelopes")]
[Authorize]
public class EnvelopesController(IMediator mediator, EnvelopeMapper mapper) : ControllerBase
{
	[HttpGet]
	[EndpointSummary("List envelopes")]
	public async Task<Ok<EnvelopeListResponse>> GetEnvelopes([FromQuery] bool? isActive = null, CancellationToken cancellationToken = default)
	{
		List<Domain.Envelopes.Envelope> rows = await mediator.Send(new GetAllEnvelopesQuery(isActive), cancellationToken);
		return TypedResults.Ok(new EnvelopeListResponse { Data = rows.Select(mapper.ToResponse).ToList() });
	}

	[HttpGet("{id}")]
	[EndpointSummary("Get an envelope")]
	public async Task<Results<Ok<EnvelopeResponse>, NotFound>> GetEnvelopeById([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		Domain.Envelopes.Envelope? row = await mediator.Send(new GetEnvelopeByIdQuery(id), cancellationToken);
		return row is null ? TypedResults.NotFound() : TypedResults.Ok(mapper.ToResponse(row));
	}

	[HttpPost]
	[EndpointSummary("Create an envelope")]
	public async Task<Results<Created<EnvelopeResponse>, ConflictProblem>> CreateEnvelope([FromBody] CreateEnvelopeRequest request, CancellationToken cancellationToken = default)
	{
		try
		{
			Domain.Envelopes.Envelope created = await mediator.Send(new CreateEnvelopeCommand(mapper.ToDomain(request)), cancellationToken);
			return TypedResults.Created($"/api/envelopes/{created.Id}", mapper.ToResponse(created));
		}
		catch (DuplicateEntityException ex)
		{
			return ApiProblem.Conflict(ex.Message);
		}
	}

	[HttpPut("{id}")]
	[EndpointSummary("Rename or archive an envelope")]
	public async Task<Results<NoContent, NotFound, ConflictProblem>> UpdateEnvelope([FromRoute] Guid id, [FromBody] UpdateEnvelopeRequest request, CancellationToken cancellationToken = default)
	{
		try
		{
			bool updated = await mediator.Send(new UpdateEnvelopeCommand(mapper.ToDomain(id, request)), cancellationToken);
			return updated ? TypedResults.NoContent() : TypedResults.NotFound();
		}
		catch (DuplicateEntityException ex)
		{
			return ApiProblem.Conflict(ex.Message);
		}
	}

	[HttpDelete("{id}")]
	[EndpointSummary("Delete an unused envelope")]
	public async Task<Results<NoContent, NotFound, ConflictProblem>> DeleteEnvelope([FromRoute] Guid id, CancellationToken cancellationToken = default)
	{
		try
		{
			bool deleted = await mediator.Send(new DeleteEnvelopeCommand(id), cancellationToken);
			return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
		}
		catch (EnvelopeException ex) when (ex.Status == StatusCodes.Status409Conflict)
		{
			return ApiProblem.Conflict(ex.Message, ex.Extensions);
		}
	}
}
