using System.Security.Claims;
using API.Http;
using Application.Classification;
using Application.Taxonomy;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

public sealed record CategoryAssistanceSettingsRequest(bool Enabled, string? Provider, string? Model,
	int DailyLimit, int PendingLimit, Guid ExpectedRevision);
public sealed record CategoryAssistanceStatusResponse(string Capability, bool ConsentEnabled,
		bool SuggestionsAvailable, string Provider, IReadOnlyList<string> Models,
		int DailyLimit, int PendingLimit);
public sealed record CategoryAssistanceDismissRequest(Guid OperationId);

[ApiVersion("1.0")]
[ApiController]
[Route("api/category-assistance")]
[Authorize]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class CategoryAssistanceController(ICategoryAssistanceSettingsStore settingsStore,
	ICategoryAssistanceReadStore readStore, ICategoryAssistanceCapabilitySource capabilitySource,
	ICategoryAssistanceDecisionStore decisionStore,
	ICategoryAssistanceReviewPreferenceStore reviewPreferenceStore) : ControllerBase
{
	[HttpGet("status")]
	[EndpointSummary("Read category assistance capability and consent status")]
	public async Task<Ok<CategoryAssistanceStatusResponse>> Status(CancellationToken ct)
	{
		var settings = await settingsStore.GetAsync(ct);
		var capability = await capabilitySource.GetAsync(ct);
		return TypedResults.Ok(new CategoryAssistanceStatusResponse(
			capability.State.ToString().ToLowerInvariant(), settings.Enabled,
			CategoryAssistancePolicy.CanSuggest(settings, capability), capability.Provider,
						capability.Models, settings.DailyLimit, settings.PendingLimit));
	}

	[HttpGet("settings")]
	[Authorize(Policy = "RequireAdmin")]
	[ProducesResponseType(StatusCodes.Status403Forbidden)]
	[EndpointSummary("Read deployment-wide category assistance settings")]
	public async Task<Ok<CategoryAssistanceSettings>> Settings(CancellationToken ct) =>
		TypedResults.Ok(await settingsStore.GetAsync(ct));

	[HttpPut("settings")]
	[Authorize(Policy = "RequireAdmin")]
	[EndpointSummary("Update audited category assistance consent and limits")]
	public async Task<Results<Ok<CategoryAssistanceSettings>, BadRequestProblem, ForbiddenProblem, ConflictProblem>>
		UpdateSettings([FromBody] CategoryAssistanceSettingsRequest request, CancellationToken ct)
	{
		try
		{
			var proposed = new CategoryAssistanceSettings(request.Enabled, request.Provider, request.Model,
				request.DailyLimit, request.PendingLimit, request.ExpectedRevision);
			var saved = await settingsStore.UpdateAsync(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
				request.ExpectedRevision, proposed, ct);
			return TypedResults.Ok(saved);
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

	[HttpGet("suggestions")]
	[EndpointSummary("List durable category suggestions for review")]
	public async Task<Results<Ok<CategoryAssistanceSuggestionPage>, BadRequestProblem>> Suggestions(
		[FromQuery] int offset = 0, [FromQuery] int limit = 25,
		[FromQuery] string review = "active", CancellationToken ct = default)
	{
		if (offset < 0 || limit is < 1 or > 100 || review is not ("active" or "later" or "left_as_is" or "all"))
		{
			return ApiProblem.BadRequest("Offset must be nonnegative, limit must be 1 through 100, and review must be active, later, left_as_is or all.");
		}

		var settings = await settingsStore.GetAsync(ct);
		var capability = await capabilitySource.GetAsync(ct);
		if (!CategoryAssistancePolicy.CanSuggest(settings, capability))
		{
			return TypedResults.Ok(new CategoryAssistanceSuggestionPage(0, offset, limit, []));
		}

		var actor = User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
		return TypedResults.Ok(await readStore.ListSuggestionsAsync(actor, review, offset, limit, ct));
	}

	[HttpGet("activity")]
	[Authorize(Policy = "RequireAdmin")]
	[ProducesResponseType(StatusCodes.Status403Forbidden)]
	[EndpointSummary("Read aggregate category assistance activity")]
	public async Task<Ok<CategoryAssistanceActivity>> Activity(CancellationToken ct) =>
		TypedResults.Ok(await readStore.GetActivityAsync(ct));

	[HttpGet("suggestions/{attemptId:guid}")]
	[EndpointSummary("Read one durable category suggestion for review")]
	public async Task<Results<Ok<CategoryAssistanceSuggestionRead>, NotFoundProblem>> Suggestion(
			Guid attemptId, CancellationToken ct)
	{
		var settings = await settingsStore.GetAsync(ct);
		var capability = await capabilitySource.GetAsync(ct);
		if (!CategoryAssistancePolicy.CanSuggest(settings, capability))
		{
			return ApiProblem.NotFound("Suggestion does not exist.");
		}

		var actor = User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
		var suggestion = await readStore.GetSuggestionAsync(actor, attemptId, ct);
		return suggestion is null
				? ApiProblem.NotFound("Suggestion does not exist.")
				: TypedResults.Ok(suggestion);
	}

	[HttpGet("suggestions/{attemptId:guid}/items")]
	[EndpointSummary("Page eligible receipt items for an assistance suggestion")]
	public async Task<Results<Ok<CategoryAssistanceEligibleItemPage>, BadRequestProblem, NotFoundProblem>>
		EligibleItems(Guid attemptId, [FromQuery] int offset = 0, [FromQuery] int limit = 25,
			CancellationToken ct = default)
	{
		try
		{
			return TypedResults.Ok(await decisionStore.ListEligibleItemsAsync(
			User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", attemptId, offset, limit, ct));
		}
		catch (TaxonomyException error) { return error.Status == 404 ? ApiProblem.NotFound(error.Message) : ApiProblem.BadRequest(error.Message); }
	}

	[HttpGet("suggestions/{attemptId:guid}/review-preference")]
	[EndpointSummary("Read your category assistance review preference")]
	public async Task<Results<Ok<CategoryAssistanceReviewPreference>, BadRequestProblem, ForbiddenProblem,
		NotFoundProblem, ConflictProblem>> ReviewPreference(Guid attemptId, CancellationToken ct)
	{
		try
		{
			return TypedResults.Ok(await reviewPreferenceStore.GetAsync(
			User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", attemptId, ct));
		}
		catch (TaxonomyException error) { return ReviewError(error); }
	}

	[HttpPut("suggestions/{attemptId:guid}/review-preference")]
	[EndpointSummary("Set your category assistance review preference and excluded items")]
	public async Task<Results<Ok<CategoryAssistanceReviewPreference>, BadRequestProblem, ForbiddenProblem,
		NotFoundProblem, ConflictProblem>> UpdateReviewPreference(Guid attemptId,
			[FromBody] CategoryAssistanceReviewPreferenceRequest request, CancellationToken ct)
	{
		try
		{
			return TypedResults.Ok(await reviewPreferenceStore.UpdateAsync(
			User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", attemptId, request, ct));
		}
		catch (TaxonomyException error) { return ReviewError(error); }
	}

	private static Results<Ok<CategoryAssistanceReviewPreference>, BadRequestProblem, ForbiddenProblem,
		NotFoundProblem, ConflictProblem> ReviewError(TaxonomyException error) => error.Status switch
		{
			403 => new ForbiddenProblem(error.Message, "writer_required"),
			404 => ApiProblem.NotFound(error.Message),
			409 => ApiProblem.Conflict(error.Message),
			_ => ApiProblem.BadRequest(error.Message)
		};

	[HttpPost("suggestions/{attemptId:guid}/accept")]
	[EndpointSummary("Accept a suggestion for explicitly selected receipt items")]
	public async Task<Results<Ok<CategoryAssistanceDecisionResult>, BadRequestProblem, ForbiddenProblem,
		NotFoundProblem, ConflictProblem>> Accept(Guid attemptId,
			[FromBody] CategoryAssistanceDecisionRequest request, CancellationToken ct)
	{
		try
		{
			return TypedResults.Ok(await decisionStore.AcceptAsync(
			User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", attemptId, request, ct));
		}
		catch (TaxonomyException error) { return DecisionError(error); }
	}

	[HttpPost("suggestions/{attemptId:guid}/dismiss")]
	[EndpointSummary("Dismiss a category assistance suggestion")]
	public async Task<Results<Ok<CategoryAssistanceDecisionResult>, BadRequestProblem, ForbiddenProblem,
		NotFoundProblem, ConflictProblem>> Dismiss(Guid attemptId,
			[FromBody] CategoryAssistanceDismissRequest request, CancellationToken ct)
	{
		try
		{
			return TypedResults.Ok(await decisionStore.DismissAsync(
			User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", attemptId, request.OperationId, ct));
		}
		catch (TaxonomyException error) { return DecisionError(error); }
	}

	private static Results<Ok<CategoryAssistanceDecisionResult>, BadRequestProblem, ForbiddenProblem,
		NotFoundProblem, ConflictProblem> DecisionError(TaxonomyException error) => error.Status switch
		{
			403 => new ForbiddenProblem(error.Message, "writer_required"),
			404 => ApiProblem.NotFound(error.Message),
			409 => ApiProblem.Conflict(error.Message),
			_ => ApiProblem.BadRequest(error.Message)
		};
}
