using System.Security.Claims;
using API.Generated.Dtos;
using API.Http;
using Application.Normalization;
using Application.Taxonomy;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Source = Application.Normalization.NormalizationSourceKey;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/category-mappings")]
[Authorize]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class CategoryMappingsController(ICategoryMappingService service) : ControllerBase
{
	[HttpPost("inspect")]
	[EndpointSummary("Inspect exact-source category approval")]
	public async Task<Results<Ok<CategoryMappingInspectionResponse>, BadRequestProblem>> Inspect(
		[FromBody] CategoryMappingSourceRequest request, CancellationToken ct)
	{
		try
		{
			Source source = Normalize(request.Source);
			string fingerprint = NormalizationCatalogPolicy.Hash(source);
			CategoryMappingResolution resolution = await service.ResolveAsync(source, fingerprint, ct: ct);
			// Advisory reads may observe different heads; callers must use CAS when changing a mapping.
			CategoryMappingHistory history = await service.ReadHistoryAsync(source, null, 1, ct);
			return TypedResults.Ok(new CategoryMappingInspectionResponse
			{
				Source = Map(source),
				SourceFingerprint = fingerprint,
				Disposition = resolution.Disposition,
				TaxonomyRevisionId = resolution.TaxonomyRevisionId,
				Category = resolution.CategoryId is Guid category ? new() { Id = category } : null,
				Mapping = history.Items.Count == 0 ? null! : Map(history.Items[0])
			});
		}
		catch (NormalizationCatalogException error) { return ApiProblem.BadRequest(error.Message); }
	}

	[HttpPost("history")]
	[EndpointSummary("Read exact-source approval history")]
	public async Task<Results<Ok<CategoryMappingHistoryResponse>, BadRequestProblem>> History(
		[FromBody] CategoryMappingHistoryRequest request, CancellationToken ct)
	{
		try
		{
			CategoryMappingHistory history = await service.ReadHistoryAsync(Normalize(request.Source), request.BeforeRevisionId, request.Limit, ct);
			return TypedResults.Ok(new CategoryMappingHistoryResponse { Items = history.Items.Select(Map).ToList(), NextBeforeRevisionId = history.NextBeforeRevisionId });
		}
		catch (NormalizationCatalogException error) { return ApiProblem.BadRequest(error.Message); }
		catch (TaxonomyException error) { return ApiProblem.BadRequest(error.Message); }
	}

	[HttpPost("changes")]
	[Authorize(Policy = "RequireAdmin")]
	[EndpointSummary("Explicitly approve or revoke future exact-source category reuse")]
	public async Task<Results<Ok<CategoryMappingRevisionResponse>, BadRequestProblem, ForbiddenProblem, ConflictProblem>> Changes(
		[FromBody] CategoryMappingChangeRequest request, CancellationToken ct)
	{
		try
		{
			CategoryMappingRevision revision = await service.ChangeAsync(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
				new(Normalize(request.Source), request.SourceFingerprint, request.ExpectedMappingRevisionId,
					request.ExpectedTaxonomyRevisionId, request.Category?.Id, request.OperationId, request.Reason), ct);
			return TypedResults.Ok(Map(revision));
		}
		catch (NormalizationCatalogException error) { return ApiProblem.BadRequest(error.Message); }
		catch (TaxonomyException error)
		{
			return error.Status switch
			{
				403 => new ForbiddenProblem(error.Message, "enabled_actor_required"),
				409 => ApiProblem.Conflict(error.Message, error.Extensions),
				_ => ApiProblem.BadRequest(error.Message)
			};
		}
	}

	private static Source Normalize(API.Generated.Dtos.NormalizationSourceKey source)
	{
		if (source == null || source.Location == null || string.IsNullOrWhiteSpace(source.Description)
			|| source.Location.Contains('\0') || source.Description.Contains('\0') || source.ItemCode?.Contains('\0') == true)
		{ throw new NormalizationCatalogException(400, "invalid_source", "Select a valid nonempty source without NUL characters."); }
		return NormalizationCatalogPolicy.Normalize(new(source.Location, source.Description, source.ItemCode));
	}
	private static API.Generated.Dtos.NormalizationSourceKey Map(Source source) => new() { Location = source.Location, Description = source.Description, ItemCode = source.ItemCode };
	private static CategoryMappingRevisionResponse Map(CategoryMappingRevision revision) => new()
	{
		Id = revision.Id,
		PreviousRevisionId = revision.PreviousRevisionId,
		Source = Map(revision.Source),
		SourceFingerprint = revision.SourceFingerprint,
		Category = revision.CategoryId is Guid category ? new() { Id = category } : null,
		TaxonomyRevisionId = revision.TaxonomyRevisionId,
		SemanticFingerprint = revision.SemanticFingerprint,
		Actor = revision.Actor,
		OperationId = revision.OperationId,
		Reason = revision.Reason,
		RecordedAt = revision.RecordedAt
	};
}
