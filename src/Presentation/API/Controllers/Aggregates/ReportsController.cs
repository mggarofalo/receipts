using API.Generated.Dtos;
using Application.Commands.Reports;
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
	[HttpGet("health-summary")]
	[EndpointSummary("Get data-quality health summary")]
	[EndpointDescription("Returns headline counts for the data-quality reports so the reports hub can show at a glance whether anything needs attention: out-of-balance receipts, duplicate receipt groups (matched on date + location), and uncategorized receipt items. Backed by COUNT queries only — no report rows are returned.")]
	public async Task<Ok<ReportsHealthSummaryResponse>> GetHealthSummary(CancellationToken cancellationToken)
	{
		GetReportsHealthSummaryQuery query = new();
		AppReports.ReportsHealthSummaryResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new ReportsHealthSummaryResponse
		{
			OutOfBalanceCount = result.OutOfBalanceCount,
			DuplicateGroupCount = result.DuplicateGroupCount,
			UncategorizedItemCount = result.UncategorizedItemCount
		});
	}

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
	[EndpointDescription("Returns time-series cost data for a specific item description, normalized description, or category.")]
	public async Task<Results<Ok<ItemCostOverTimeResponse>, BadRequest<string>>> GetItemCostOverTime(
		[FromQuery] string? description,
		[FromQuery] string? category,
		[FromQuery] DateOnly? startDate,
		[FromQuery] DateOnly? endDate,
		[FromQuery] string? granularity,
		[FromQuery] string? normalizedDescription,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrEmpty(description) && string.IsNullOrEmpty(category) && string.IsNullOrEmpty(normalizedDescription))
		{
			return TypedResults.BadRequest("One of description, normalizedDescription, or category is required");
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

		GetItemCostOverTimeQuery query = new(description, category, startDate, endDate, gran, normalizedDescription);
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
		[FromQuery] bool? includeAccepted,
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

		GetDuplicateDetectionReportQuery query = new(match, locTol, totTol, includeAccepted ?? false);
		AppReports.DuplicateDetectionResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new DuplicatesResponse
		{
			GroupCount = result.GroupCount,
			TotalDuplicateReceipts = result.TotalDuplicateReceipts,
			Groups = result.Groups.Select(g => new DuplicateGroup
			{
				MatchKey = g.MatchKey,
				IsAccepted = g.IsAccepted,
				Receipts = g.Receipts.Select(ToDuplicateReceipt).ToList()
			}).ToList()
		});
	}

	[HttpGet("duplicates/accepted")]
	[EndpointSummary("List accepted duplicate groups")]
	[EndpointDescription("Returns the receipt groups a user has accepted as genuinely separate purchases.")]
	public async Task<Ok<AcceptedDuplicatesResponse>> GetAcceptedDuplicates(CancellationToken cancellationToken)
	{
		AppReports.AcceptedDuplicatesResult result = await mediator.Send(new GetAcceptedDuplicatesQuery(), cancellationToken);

		return TypedResults.Ok(new AcceptedDuplicatesResponse
		{
			GroupCount = result.GroupCount,
			Groups = result.Groups.Select(g => new AcceptedDuplicateGroup
			{
				AcceptedAt = g.AcceptedAt,
				Receipts = g.Receipts.Select(ToDuplicateReceipt).ToList(),
				MemberReceiptIds = g.MemberReceiptIds
			}).ToList()
		});
	}

	[HttpPost("duplicates/accepted")]
	[EndpointSummary("Accept a duplicate group as not-a-duplicate")]
	[EndpointDescription("Records every pair of the supplied receipts as \"not a duplicate\" so the group stops being reported. Idempotent.")]
	public async Task<Results<Ok<AcceptDuplicateGroupResponse>, NotFound<string>>> AcceptDuplicateGroup(
		[FromBody] AcceptDuplicateGroupRequest request,
		CancellationToken cancellationToken)
	{
		try
		{
			int acceptedPairCount = await mediator.Send(
				new AcceptDuplicateGroupCommand(DistinctReceiptIds(request)), cancellationToken);
			return TypedResults.Ok(new AcceptDuplicateGroupResponse { AcceptedPairCount = acceptedPairCount });
		}
		catch (KeyNotFoundException ex)
		{
			return TypedResults.NotFound(ex.Message);
		}
	}

	[HttpPost("duplicates/accepted/remove")]
	[EndpointSummary("Undo a duplicate-group acceptance")]
	[EndpointDescription("Removes the \"not a duplicate\" assertion between every pair of the supplied receipts, and only those pairs. Send a group's memberReceiptIds to undo it whole, or a cluster's receipts to un-accept just that cluster.")]
	public async Task<Ok<UnacceptDuplicateGroupResponse>> UnacceptDuplicateGroup(
		[FromBody] UnacceptDuplicateGroupRequest request,
		CancellationToken cancellationToken)
	{
		// Bound to its OWN request type, not AcceptDuplicateGroupRequest. Sharing one contract meant
		// sharing the accept validator's 100-ID cap, which made any accepted group larger than that
		// impossible to undo — and groups grow past it by component merging, without any single
		// accept call exceeding the cap.
		int removedPairCount = await mediator.Send(
			new UnacceptDuplicateGroupCommand([.. request.ReceiptIds.Distinct()]), cancellationToken);
		return TypedResults.Ok(new UnacceptDuplicateGroupResponse { RemovedPairCount = removedPairCount });
	}

	/// <summary>
	/// Shape normalization only. Bounds and per-element checks live in
	/// <c>AcceptDuplicateGroupRequestValidator</c>, which the global FluentValidation action filter
	/// runs before this method — so a request that reaches here already satisfies them, and every
	/// rejection returns one ValidationProblemDetails shape instead of two different 400 bodies.
	/// </summary>
	private static List<Guid> DistinctReceiptIds(AcceptDuplicateGroupRequest request) =>
		[.. request.ReceiptIds.Distinct()];

	private static DuplicateReceipt ToDuplicateReceipt(AppReports.DuplicateReceiptSummary summary) =>
		new()
		{
			ReceiptId = summary.ReceiptId,
			Location = summary.Location,
			Date = summary.Date,
			TransactionTotal = (double)summary.TransactionTotal
		};

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
		[FromQuery] string? sortBy,
		[FromQuery] string? sortDirection,
		[FromQuery] int? page,
		[FromQuery] int? pageSize,
		CancellationToken cancellationToken)
	{
		string sort = sortBy ?? "totalAmount";
		string direction = sortDirection ?? "desc";
		int pg = page ?? 1;
		int ps = pageSize ?? 50;

		string[] validSortColumns = ["canonicalname", "totalamount", "itemcount"];
		if (!validSortColumns.Contains(sort.ToLowerInvariant()))
		{
			return TypedResults.BadRequest($"Invalid sortBy '{sort}'. Allowed: canonicalName, totalAmount, itemCount");
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

		if (from.HasValue && to.HasValue && from.Value > to.Value)
		{
			return TypedResults.BadRequest("from must be before or equal to to");
		}

		GetSpendingByNormalizedDescriptionQuery query = new(from, to, sort, direction, pg, ps);
		AppReports.SpendingByNormalizedDescriptionResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new SpendingByNormalizedDescriptionResponse
		{
			TotalCount = result.TotalCount,
			GrandTotal = (double)result.GrandTotal,
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
