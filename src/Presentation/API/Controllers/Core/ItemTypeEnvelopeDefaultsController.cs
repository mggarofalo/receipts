using API.Generated.Dtos;
using Application.Commands.ItemTypeEnvelopeDefault.Delete;
using Application.Commands.ItemTypeEnvelopeDefault.Set;
using Application.Envelopes;
using Application.Queries.Core.ItemTypeEnvelopeDefault;
using Asp.Versioning;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ItemTypeEnvelopeDefault = Application.Interfaces.Services.ItemTypeEnvelopeDefault;

namespace API.Controllers.Core;

[ApiVersion("1.0")]
[ApiController]
[Route("api/item-type-envelope-defaults")]
[Authorize]
public class ItemTypeEnvelopeDefaultsController(IMediator mediator) : ControllerBase
{
	[HttpGet]
	[EndpointSummary("List item-type default envelopes")]
	public async Task<Ok<ItemTypeEnvelopeDefaultListResponse>> GetItemTypeEnvelopeDefaults(CancellationToken cancellationToken = default)
	{
		List<ItemTypeEnvelopeDefault> rows = await mediator.Send(new GetItemTypeEnvelopeDefaultsQuery(), cancellationToken);
		return TypedResults.Ok(new ItemTypeEnvelopeDefaultListResponse { Data = rows.Select(ToResponse).ToList() });
	}

	[HttpPut("{categoryId}")]
	[EndpointSummary("Set an item type's default envelope")]
	public async Task<Results<Ok<ItemTypeEnvelopeDefaultResponse>, BadRequestProblem>> SetItemTypeEnvelopeDefault([FromRoute] Guid categoryId,
		[FromBody] SetItemTypeEnvelopeDefaultRequest request, CancellationToken cancellationToken = default)
	{
		try
		{
			ItemTypeEnvelopeDefault row = await mediator.Send(new SetItemTypeEnvelopeDefaultCommand(categoryId, request.EnvelopeId), cancellationToken);
			return TypedResults.Ok(ToResponse(row));
		}
		catch (EnvelopeException ex) when (ex.Status == StatusCodes.Status400BadRequest)
		{
			return ApiProblem.BadRequest(ex.Message);
		}
	}

	[HttpDelete("{categoryId}")]
	[EndpointSummary("Remove an item type's default envelope")]
	public async Task<Results<NoContent, NotFound>> DeleteItemTypeEnvelopeDefault([FromRoute] Guid categoryId, CancellationToken cancellationToken = default)
	{
		bool deleted = await mediator.Send(new DeleteItemTypeEnvelopeDefaultCommand(categoryId), cancellationToken);
		return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
	}

	private static ItemTypeEnvelopeDefaultResponse ToResponse(ItemTypeEnvelopeDefault row)
		=> new() { CategoryId = row.CategoryId, EnvelopeId = row.EnvelopeId, UpdatedAt = row.UpdatedAt };
}
