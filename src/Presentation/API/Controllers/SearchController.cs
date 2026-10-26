using System.Globalization;
using API.Generated.Dtos;
using API.Http;
using Application.Models.Reports;
using Application.Queries.Search;
using Asp.Versioning;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PurchaseSearchOptions = Application.Models.Search.PurchaseSearchOptions;

namespace API.Controllers;

[ApiVersion("1.0")]
[ApiController]
[Route("api/search")]
[Authorize]
public class SearchController(IMediator mediator) : ControllerBase
{
	[HttpGet("purchases")]
	[EndpointSummary("Search purchase history")]
	public async Task<Results<Ok<PurchaseSearchResponse>, BadRequestProblem>> SearchPurchases(
		[FromQuery] string? q,
		CancellationToken cancellationToken,
		[FromQuery] int offset = 0,
				[FromQuery] int limit = 25,
				[FromQuery] string sortBy = "date",
				[FromQuery] string sortDirection = "desc",
				[FromQuery] string? location = null,
				[FromQuery] DateOnly? startDate = null,
				[FromQuery] DateOnly? endDate = null,
				[FromQuery] decimal? minUnitPrice = null,
				[FromQuery] decimal? maxUnitPrice = null,
				[FromQuery] bool includeUnreviewed = false,
								[FromQuery] Guid? normalizedDescriptionId = null,
								[FromQuery] string? categoryView = null,
								[FromQuery] Guid? categoryId = null,
								[FromQuery] string? knowledgeCutoff = null,
								[FromQuery] Guid? capturedCurrentRevisionId = null)
	{
		CategoryReportView view = categoryView switch
		{
			null or "asClassified" => CategoryReportView.AsClassified,
			"currentHierarchy" => CategoryReportView.CurrentHierarchy,
			_ => (CategoryReportView)(-1)
		};
		if (!Enum.IsDefined(view)) { return ApiProblem.BadRequest("categoryView must be asClassified or currentHierarchy"); }
		DateTimeOffset? cutoff = null;
		if (knowledgeCutoff is not null)
		{
			if (!knowledgeCutoff.EndsWith('Z') || !DateTimeOffset.TryParseExact(knowledgeCutoff,
					["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
					CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
			{ return ApiProblem.BadRequest("knowledgeCutoff must be an ISO UTC timestamp with a Z suffix"); }
			cutoff = parsed.ToUniversalTime();
		}
		if (categoryId == Guid.Empty || capturedCurrentRevisionId == Guid.Empty)
		{ return ApiProblem.BadRequest("categoryId and capturedCurrentRevisionId must be non-empty UUIDs"); }
		if (capturedCurrentRevisionId.HasValue && view != CategoryReportView.CurrentHierarchy)
		{ return ApiProblem.BadRequest("capturedCurrentRevisionId requires currentHierarchy"); }
		Application.Models.Search.PurchaseSearchResult result;
		try
		{
			result = await mediator.Send(
	new SearchPurchasesQuery(q, offset, limit,
						new PurchaseSearchOptions(sortBy, sortDirection, location, startDate, endDate, minUnitPrice, maxUnitPrice, includeUnreviewed, normalizedDescriptionId,
								view, categoryId, cutoff, capturedCurrentRevisionId)),
				cancellationToken);
		}
		catch (ArgumentException exception) when (exception.ParamName == "CapturedCurrentRevisionId")
		{ return ApiProblem.BadRequest(exception.Message); }
		return TypedResults.Ok(new PurchaseSearchResponse
		{
			CategoryView = result.CategoryView == CategoryReportView.CurrentHierarchy
								? PurchaseSearchResponseCategoryView.CurrentHierarchy : PurchaseSearchResponseCategoryView.AsClassified,
			CurrentRevisionId = result.CurrentRevisionId,
			KnowledgeCutoff = result.KnowledgeCutoff,
			AssignmentSelection = result.AssignmentSelection == CategoryReportAssignmentSelection.LatestRecordedAtOrBeforeCutoff
								? PurchaseSearchResponseAssignmentSelection.LatestRecordedAtOrBeforeCutoff
								: PurchaseSearchResponseAssignmentSelection.CurrentPointer,
			Items = result.Items.Select(x => new PurchaseSearchItem
			{
				Id = x.Id,
				ReceiptId = x.ReceiptId,
				Date = x.Date,
				Location = x.Location,
				Description = x.Description,
				ReceiptItemCode = x.ReceiptItemCode,
				NormalizedDescriptionId = x.NormalizedDescriptionId,
				NormalizedDescription = x.NormalizedDescription,
				NormalizationReviewState = x.NormalizationReviewState,
				Quantity = (double)x.Quantity,
				UnitPrice = (double)x.UnitPrice,
				UnitPriceCurrency = x.UnitPriceCurrency,
				TotalAmount = (double)x.TotalAmount,
				TotalAmountCurrency = x.TotalAmountCurrency,
				CategoryId = x.CategoryId,
				PathRevisionId = x.PathRevisionId,
				CategoryState = x.CategoryState switch
				{
					CategoryReportState.Classified => PurchaseSearchItemCategoryState.Classified,
					CategoryReportState.Unclassified => PurchaseSearchItemCategoryState.Unclassified,
					CategoryReportState.UnresolvedLegacy => PurchaseSearchItemCategoryState.UnresolvedLegacy,
					CategoryReportState.UnresolvedPath => PurchaseSearchItemCategoryState.UnresolvedPath,
					CategoryReportState.BeforeMigrationHistory => PurchaseSearchItemCategoryState.BeforeMigrationHistory,
					_ => PurchaseSearchItemCategoryState.MissingAssignment,
				},
				CategoryPath = x.CategoryPath.Select(node => new CategoryReportPathNode
				{ CategoryId = node.Id, Name = node.Name, Archived = node.Archived }).ToList(),
			}).ToList(),
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}
}
