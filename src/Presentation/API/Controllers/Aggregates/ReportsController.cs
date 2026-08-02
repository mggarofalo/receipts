using API.Generated.Dtos;
using Application.Queries.Aggregates.Reports;
using Asp.Versioning;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using AppReports = Application.Models.Reports;

namespace API.Controllers.Aggregates;

[ApiVersion("1.0")]
[ApiController]
[Route("api/reports")]
[Produces("application/json")]
[Authorize]
public class ReportsController(IMediator mediator) : ControllerBase
{
	[HttpGet("out-of-balance")]
	[EndpointSummary("Get out-of-balance receipts report")]
	[EndpointDescription("Returns all receipts where item subtotal + tax + adjustments does not equal the transaction total.")]
	public async Task<Results<Ok<OutOfBalanceResponse>, BadRequest<string>>> GetOutOfBalance(
		[FromQuery] string? sortBy,
		[FromQuery] string? sortDirection,
		[FromQuery] int? page,
		[FromQuery] int? pageSize,
		CancellationToken cancellationToken)
	{
		string sort = sortBy ?? "date";
		string direction = sortDirection ?? "asc";
		int pg = page ?? 1;
		int ps = pageSize ?? 50;

		string[] validSortColumns = ["date", "difference"];
		if (!validSortColumns.Contains(sort.ToLowerInvariant()))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sort}'. Allowed: date, difference");
		}

		string[] validDirections = ["asc", "desc"];
		if (!validDirections.Contains(direction.ToLowerInvariant()))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{direction}'. Allowed: asc, desc");
		}

		if (pg < 1)
		{
			return TypedResults.BadRequest("page must be at least 1");
		}

		if (ps < 1 || ps > 100)
		{
			return TypedResults.BadRequest("pageSize must be between 1 and 100");
		}

		GetOutOfBalanceReportQuery query = new(sort, direction, pg, ps);
		AppReports.OutOfBalanceResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new OutOfBalanceResponse
		{
			TotalCount = result.TotalCount,
			TotalDiscrepancy = (double)result.TotalDiscrepancy,
			Items = result.Items.Select(i => new OutOfBalanceItem
			{
				ReceiptId = i.ReceiptId,
				Location = i.Location,
				Date = i.Date,
				ItemSubtotal = (double)i.ItemSubtotal,
				TaxAmount = (double)i.TaxAmount,
				AdjustmentTotal = (double)i.AdjustmentTotal,
				ExpectedTotal = (double)i.ExpectedTotal,
				TransactionTotal = (double)i.TransactionTotal,
				Difference = (double)i.Difference
			}).ToList()
		});
	}

	[HttpGet("spending-by-location")]
	[EndpointSummary("Get spending by location report")]
	[EndpointDescription("Returns spending aggregated by store location with visit count, total, and average per visit.")]
	public async Task<Results<Ok<SpendingByLocationResponse>, BadRequest<string>>> GetSpendingByLocation(
		[FromQuery] DateOnly? startDate,
		[FromQuery] DateOnly? endDate,
		[FromQuery] string? sortBy,
		[FromQuery] string? sortDirection,
		[FromQuery] int? page,
		[FromQuery] int? pageSize,
		CancellationToken cancellationToken)
	{
		string sort = sortBy ?? "total";
		string direction = sortDirection ?? "desc";
		int pg = page ?? 1;
		int ps = pageSize ?? 50;

		string[] validSortColumns = ["location", "visits", "total", "averagepervisit"];
		if (!validSortColumns.Contains(sort.ToLowerInvariant()))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sort}'. Allowed: location, visits, total, averagePerVisit");
		}

		string[] validDirections = ["asc", "desc"];
		if (!validDirections.Contains(direction.ToLowerInvariant()))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{direction}'. Allowed: asc, desc");
		}

		if (pg < 1)
		{
			return TypedResults.BadRequest("page must be at least 1");
		}

		if (ps < 1 || ps > 100)
		{
			return TypedResults.BadRequest("pageSize must be between 1 and 100");
		}

		if (startDate.HasValue && endDate.HasValue && startDate > endDate)
		{
			return TypedResults.BadRequest("startDate must be before or equal to endDate");
		}

		GetSpendingByLocationReportQuery query = new(startDate, endDate, sort, direction, pg, ps);
		AppReports.SpendingByLocationResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new SpendingByLocationResponse
		{
			TotalCount = result.TotalCount,
			GrandTotal = (double)result.GrandTotal,
			Items = result.Items.Select(i => new SpendingByLocationItem
			{
				Location = i.Location,
				Visits = i.Visits,
				Total = (double)i.Total,
				AveragePerVisit = (double)i.AveragePerVisit
			}).ToList()
		});
	}

	[HttpGet("item-descriptions")]
	[EndpointSummary("Search item descriptions for autocomplete")]
	[EndpointDescription("Returns distinct item descriptions with their category and occurrence count, filtered by a search term (minimum 2 characters).")]
	public async Task<Results<Ok<ItemDescriptionsResponse>, BadRequest<string>>> GetItemDescriptions(
		[FromQuery] string? search,
		[FromQuery] bool? categoryOnly,
		[FromQuery] int? limit,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(search) || search.Length < 2)
		{
			return TypedResults.BadRequest("search must be at least 2 characters");
		}

		int lim = limit ?? 20;
		if (lim < 1 || lim > 50)
		{
			return TypedResults.BadRequest("limit must be between 1 and 50");
		}

		GetItemDescriptionsQuery query = new(search, categoryOnly ?? false, lim);
		AppReports.ItemDescriptionResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new ItemDescriptionsResponse
		{
			Items = result.Items.Select(i => new ItemDescriptionItem
			{
				Description = i.Description,
				Category = i.Category,
				Occurrences = i.Occurrences
			}).ToList()
		});
	}

	[HttpGet("item-cost-over-time")]
	[EndpointSummary("Get item cost over time")]
	[EndpointDescription("Returns time-series cost data for a specific item description or category.")]
	public async Task<Results<Ok<ItemCostOverTimeResponse>, BadRequest<string>>> GetItemCostOverTime(
		[FromQuery] string? description,
		[FromQuery] string? category,
		[FromQuery] DateOnly? startDate,
		[FromQuery] DateOnly? endDate,
		[FromQuery] string? granularity,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrEmpty(description) && string.IsNullOrEmpty(category))
		{
			return TypedResults.BadRequest("Either description or category is required");
		}

		string gran = granularity ?? "exact";
		string[] validGranularities = ["exact", "monthly", "yearly"];
		if (!validGranularities.Contains(gran.ToLowerInvariant()))
		{
			return TypedResults.BadRequest($"Invalid granularity '{gran}'. Allowed: exact, monthly, yearly");
		}

		if (startDate.HasValue && endDate.HasValue && startDate.Value > endDate.Value)
		{
			return TypedResults.BadRequest("startDate must be before or equal to endDate");
		}

		GetItemCostOverTimeQuery query = new(description, category, startDate, endDate, gran);
		AppReports.ItemCostOverTimeResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new ItemCostOverTimeResponse
		{
			Buckets = result.Buckets.Select(b => new ItemCostBucket
			{
				Period = b.Period,
				Amount = (double)b.Amount
			}).ToList()
		});
	}

	[HttpGet("duplicates")]
	[EndpointSummary("Get duplicate receipt detection report")]
	[EndpointDescription("Finds receipts that share the same date+location, date+total, or all three fields and may be double entries.")]
	public async Task<Results<Ok<DuplicatesResponse>, BadRequest<string>>> GetDuplicates(
		[FromQuery] string? matchOn,
		[FromQuery] string? locationTolerance,
		[FromQuery] double? totalTolerance,
		CancellationToken cancellationToken)
	{
		string match = matchOn ?? "dateAndLocation";
		string locTol = locationTolerance ?? "exact";
		decimal totTol = (decimal)(totalTolerance ?? 0);

		string[] validMatchOn = ["dateAndLocation", "dateAndTotal", "dateAndLocationAndTotal"];
		if (!validMatchOn.Contains(match, StringComparer.OrdinalIgnoreCase))
		{
			return TypedResults.BadRequest($"Invalid matchOn '{match}'. Allowed: dateAndLocation, dateAndTotal, dateAndLocationAndTotal");
		}

		string[] validLocTolerance = ["exact", "normalized"];
		if (!validLocTolerance.Contains(locTol.ToLowerInvariant()))
		{
			return TypedResults.BadRequest($"Invalid locationTolerance '{locTol}'. Allowed: exact, normalized");
		}

		if (totTol < 0)
		{
			return TypedResults.BadRequest("totalTolerance must be >= 0");
		}

		GetDuplicateDetectionReportQuery query = new(match, locTol, totTol);
		AppReports.DuplicateDetectionResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new DuplicatesResponse
		{
			GroupCount = result.GroupCount,
			TotalDuplicateReceipts = result.TotalDuplicateReceipts,
			Groups = result.Groups.Select(g => new DuplicateGroup
			{
				MatchKey = g.MatchKey,
				Receipts = g.Receipts.Select(r => new DuplicateReceipt
				{
					ReceiptId = r.ReceiptId,
					Location = r.Location,
					Date = r.Date,
					TransactionTotal = (double)r.TransactionTotal
				}).ToList()
			}).ToList()
		});
	}

	[HttpGet("category-trends")]
	[EndpointSummary("Get category spending trends over time")]
	[EndpointDescription("Returns time-series spending data broken down by category. Categories beyond the topN threshold are collapsed into \"Other\". Buckets are dense and zero-filled.")]
	public async Task<Results<Ok<CategoryTrendsResponse>, BadRequest<string>>> GetCategoryTrends(
		[FromQuery] DateOnly? startDate,
		[FromQuery] DateOnly? endDate,
		[FromQuery] string? granularity,
		[FromQuery] int? topN,
		CancellationToken cancellationToken)
	{
		DateOnly start = startDate ?? DateOnly.MinValue;
		DateOnly end = endDate ?? DateOnly.FromDateTime(DateTime.Today);
		string gran = granularity ?? "monthly";
		int top = topN ?? 7;

		if (start > end)
		{
			return TypedResults.BadRequest("startDate must be before or equal to endDate");
		}

		string[] validGranularities = ["daily", "monthly", "quarterly", "yearly"];
		if (!validGranularities.Contains(gran.ToLowerInvariant()))
		{
			return TypedResults.BadRequest($"Invalid granularity '{gran}'. Allowed: daily, monthly, quarterly, yearly");
		}

		if (top < 1 || top > 50)
		{
			return TypedResults.BadRequest("topN must be between 1 and 50");
		}

		GetCategoryTrendsReportQuery query = new(start, end, gran, top);
		AppReports.CategoryTrendsResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new CategoryTrendsResponse
		{
			Categories = result.Categories,
			Buckets = result.Buckets.Select(b => new CategoryTrendsBucket
			{
				Period = b.Period,
				Amounts = b.Amounts.Select(a => (double)a).ToList()
			}).ToList()
		});
	}

	[HttpGet("uncategorized-items")]
	[EndpointSummary("Get uncategorized items report")]
	[EndpointDescription("Returns all receipt items where the category is \"Uncategorized\".")]
	public async Task<Results<Ok<UncategorizedItemsResponse>, BadRequest<string>>> GetUncategorizedItems(
		[FromQuery] string? sortBy,
		[FromQuery] string? sortDirection,
		[FromQuery] int? page,
		[FromQuery] int? pageSize,
		CancellationToken cancellationToken)
	{
		string sort = sortBy ?? "description";
		string direction = sortDirection ?? "asc";
		int pg = page ?? 1;
		int ps = pageSize ?? 50;

		string[] validSortColumns = ["description", "total", "itemcode"];
		if (!validSortColumns.Contains(sort.ToLowerInvariant()))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sort}'. Allowed: description, total, itemCode");
		}

		string[] validDirections = ["asc", "desc"];
		if (!validDirections.Contains(direction.ToLowerInvariant()))
		{
			return TypedResults.BadRequest($"Invalid sortDirection '{direction}'. Allowed: asc, desc");
		}

		if (pg < 1)
		{
			return TypedResults.BadRequest("page must be at least 1");
		}

		if (ps < 1 || ps > 100)
		{
			return TypedResults.BadRequest("pageSize must be between 1 and 100");
		}

		GetUncategorizedItemsReportQuery query = new(sort, direction, pg, ps);
		AppReports.UncategorizedItemsResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new UncategorizedItemsResponse
		{
			TotalCount = result.TotalCount,
			Items = result.Items.Select(i => new UncategorizedItem
			{
				Id = i.Id,
				ReceiptId = i.ReceiptId,
				ReceiptItemCode = i.ReceiptItemCode,
				Description = i.Description,
				Quantity = (double)i.Quantity,
				UnitPrice = (double)i.UnitPrice,
				TotalAmount = (double)i.TotalAmount,
				Category = i.Category,
				Subcategory = i.Subcategory
			}).ToList()
		});
	}

	[HttpGet("spending-by-normalized-description")]
	[EndpointSummary("Get spending by normalized description report")]
	[EndpointDescription("Aggregates receipt item spending grouped by normalized description canonical name. Items without a normalized description bucket into a synthetic \"(Not Normalized)\" group.")]
	public async Task<Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequest<string>>> GetSpendingByNormalizedDescription(
		[FromQuery] DateTimeOffset? from,
		[FromQuery] DateTimeOffset? to,
		CancellationToken cancellationToken)
	{
		if (from.HasValue && to.HasValue && from.Value > to.Value)
		{
			return TypedResults.BadRequest("from must be before or equal to to");
		}

		GetSpendingByNormalizedDescriptionQuery query = new(from, to);
		AppReports.SpendingByNormalizedDescriptionResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new SpendingByNormalizedDescriptionResponse
		{
			FromDate = result.FromDate,
			ToDate = result.ToDate,
			Items = result.Items.Select(i => new SpendingByNormalizedDescriptionItem
			{
				CanonicalName = i.CanonicalName,
				TotalAmount = (double)i.TotalAmount,
				Currency = i.Currency,
				ItemCount = i.ItemCount,
				FirstSeen = i.FirstSeen,
				LastSeen = i.LastSeen
			}).ToList()
		});
	}
}
