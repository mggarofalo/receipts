using System.Security.Claims;
using API.Generated.Dtos;
using API.Http;
using Application.Taxonomy;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/receipt-items/{id:guid}")]
[Authorize]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class ClassificationController(IClassificationService service) : ControllerBase
{
	[HttpPut("classification")]
	[EndpointSummary("Assign purchase classification")]
	public async Task<Results<Ok<ClassificationResponse>, BadRequestProblem, NotFoundProblem, ConflictProblem>> Assign(
		Guid id, [FromBody] ClassificationRequest request, CancellationToken cancellationToken)
	{
		try
		{
			PurchaseClassification result = await service.AssignAsync(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", id,
				new(request.CategoryId, request.TaxonomyRevisionId, request.ExpectedAssignmentId, request.OperationId, request.Reason), cancellationToken);
			return TypedResults.Ok(Map(result));
		}
		catch (TaxonomyException error)
		{
			return error.Status switch { 404 => ApiProblem.NotFound(error.Message), 409 => ApiProblem.Conflict(error.Message), _ => ApiProblem.BadRequest(error.Message) };
		}
	}

	[HttpGet("classification-history")]
	[EndpointSummary("History purchase classification")]
	public async Task<Results<Ok<ClassificationHistoryResponse>, BadRequestProblem, NotFoundProblem>> History(
		Guid id, [FromQuery] long? beforeSequence = null, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
	{
		try
		{
			ClassificationHistory result = await service.HistoryAsync(id, beforeSequence, limit, cancellationToken);
			return TypedResults.Ok(new ClassificationHistoryResponse { Items = result.Items.Select(Map).ToList(), NextBeforeSequence = result.NextBeforeSequence });
		}
		catch (TaxonomyException error)
		{ return error.Status == 404 ? ApiProblem.NotFound(error.Message) : ApiProblem.BadRequest(error.Message); }
	}

	private static ClassificationResponse Map(PurchaseClassification row) => new()
	{
		Id = row.Id,
		ReceiptItemId = row.ReceiptItemId,
		Sequence = row.Sequence,
		PreviousAssignmentId = row.PreviousAssignmentId,
		CategoryId = row.CategoryId,
		TaxonomyRevisionId = row.TaxonomyRevisionId,
		RecordedAt = row.RecordedAt,
		Actor = row.Actor,
		Source = row.Source,
		Reason = row.Reason,
		OperationId = row.OperationId,
		LegacyCategory = row.LegacyCategory,
		LegacySubcategory = row.LegacySubcategory,
		LegacyResolution = row.LegacyResolution,
		Path = row.Path.Select(node => new TaxonomyNodeResponse { Id = node.Id, ParentId = node.ParentId, Name = node.Name, Definition = node.Definition, Archived = node.Archived }).ToList()
	};
}
