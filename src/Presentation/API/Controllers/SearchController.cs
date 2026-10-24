using API.Generated.Dtos;
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
				[FromQuery] Guid? normalizedDescriptionId = null)
	{
		Application.Models.Search.PurchaseSearchResult result = await mediator.Send(
			new SearchPurchasesQuery(q, offset, limit,
				new PurchaseSearchOptions(sortBy, sortDirection, location, startDate, endDate, minUnitPrice, maxUnitPrice, includeUnreviewed, normalizedDescriptionId)),
			cancellationToken);
		return TypedResults.Ok(new PurchaseSearchResponse
		{
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
			}).ToList(),
			Total = result.Total,
			Offset = result.Offset,
			Limit = result.Limit,
		});
	}
}
