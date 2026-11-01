using System.Globalization;
using System.Text.Json;
using API.Generated.Dtos;
using API.Services;
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
[Authorize]
public class ReportsController(IMediator mediator, IEntityChangeNotifier notifier) : ControllerBase
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
	public async Task<Results<Ok<OutOfBalanceResponse>, BadRequestProblem>> GetOutOfBalance(
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
			return ApiProblem.BadRequest($"Invalid sortBy '{sort}'. Allowed: date, difference");
		}

		string[] validDirections = ["asc", "desc"];
		if (!validDirections.Contains(direction.ToLowerInvariant()))
		{
			return ApiProblem.BadRequest($"Invalid sortDirection '{direction}'. Allowed: asc, desc");
		}

		if (pg < 1)
		{
			return ApiProblem.BadRequest("page must be at least 1");
		}

		if (ps < 1 || ps > 100)
		{
			return ApiProblem.BadRequest("pageSize must be between 1 and 100");
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
	public async Task<Results<Ok<SpendingByLocationResponse>, BadRequestProblem>> GetSpendingByLocation(
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
			return ApiProblem.BadRequest($"Invalid sortBy '{sort}'. Allowed: location, visits, total, averagePerVisit");
		}

		string[] validDirections = ["asc", "desc"];
		if (!validDirections.Contains(direction.ToLowerInvariant()))
		{
			return ApiProblem.BadRequest($"Invalid sortDirection '{direction}'. Allowed: asc, desc");
		}

		if (pg < 1)
		{
			return ApiProblem.BadRequest("page must be at least 1");
		}

		if (ps < 1 || ps > 100)
		{
			return ApiProblem.BadRequest("pageSize must be between 1 and 100");
		}

		if (startDate.HasValue && endDate.HasValue && startDate > endDate)
		{
			return ApiProblem.BadRequest("startDate must be before or equal to endDate");
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
	[EndpointDescription("Returns distinct item descriptions with their occurrence count, filtered by a search term (minimum 2 characters).")]
	public async Task<Results<Ok<ItemDescriptionsResponse>, BadRequestProblem>> GetItemDescriptions(
		[FromQuery] string? search,
		[FromQuery] int? limit,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(search) || search.Length < 2)
		{
			return ApiProblem.BadRequest("search must be at least 2 characters");
		}

		int lim = limit ?? 20;
		if (lim < 1 || lim > 50)
		{
			return ApiProblem.BadRequest("limit must be between 1 and 50");
		}

		GetItemDescriptionsQuery query = new(search, lim);
		AppReports.ItemDescriptionResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new ItemDescriptionsResponse
		{
			Items = result.Items.Select(i => new ItemDescriptionItem
			{
				Description = i.Description,
				// Retired legacy label; the contract keeps the field.
				Category = "",
				Occurrences = i.Occurrences
			}).ToList()
		});
	}

	[HttpGet("item-cost-over-time")]
	[EndpointSummary("Get item cost over time")]
	[EndpointDescription("Returns time-series cost data for a specific item description, normalized description, or item type.")]
	public async Task<Results<Ok<ItemCostOverTimeResponse>, BadRequestProblem>> GetItemCostOverTime(
		[FromQuery] string? description,
		[FromQuery] DateOnly? startDate,
		[FromQuery] DateOnly? endDate,
		[FromQuery] string? granularity,
		[FromQuery] string? normalizedDescription,
			CancellationToken cancellationToken,
			[FromQuery] Guid? categoryId = null,
			[FromQuery] string? categoryView = null,
			[FromQuery] string? knowledgeCutoff = null,
			[FromQuery] Guid? capturedCurrentRevisionId = null)
	{
		if (string.IsNullOrEmpty(description) && string.IsNullOrEmpty(normalizedDescription) && !categoryId.HasValue)
		{
			return ApiProblem.BadRequest("One of description, normalizedDescription, or categoryId is required");
		}

		string gran = granularity ?? "exact";
		string[] validGranularities = ["exact", "monthly", "yearly"];
		if (!validGranularities.Contains(gran.ToLowerInvariant()))
		{
			return ApiProblem.BadRequest($"Invalid granularity '{gran}'. Allowed: exact, monthly, yearly");
		}

		if (startDate.HasValue && endDate.HasValue && startDate.Value > endDate.Value)
		{
			return ApiProblem.BadRequest("startDate must be before or equal to endDate");
		}

		AppReports.CategoryReportView view;
		switch (categoryView)
		{
			case null or "asClassified": view = AppReports.CategoryReportView.AsClassified; break;
			case "currentHierarchy": view = AppReports.CategoryReportView.CurrentHierarchy; break;
			default: return ApiProblem.BadRequest("categoryView must be asClassified or currentHierarchy");
		}
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
		if (capturedCurrentRevisionId.HasValue && view != AppReports.CategoryReportView.CurrentHierarchy)
		{ return ApiProblem.BadRequest("capturedCurrentRevisionId requires currentHierarchy"); }
		GetItemCostOverTimeQuery query = new(description, startDate, endDate, gran, normalizedDescription)
		{ CategoryOptions = new(view, CategoryId: categoryId, KnowledgeCutoff: cutoff, CapturedCurrentRevisionId: capturedCurrentRevisionId) };
		AppReports.ItemCostOverTimeResult result;
		try { result = await mediator.Send(query, cancellationToken); }
		catch (ArgumentException exception) when (exception.ParamName == "capturedCurrentRevisionId")
		{ return ApiProblem.BadRequest(exception.Message); }

		return TypedResults.Ok(new ItemCostOverTimeResponse
		{
			Buckets = result.Buckets.Select(b => new ItemCostBucket
			{
				Period = b.Period,
				Amount = (double)b.Amount,
				Currency = b.Currency
			}).ToList()
		});
	}

	[HttpGet("duplicates")]
	[EndpointSummary("Get duplicate receipt detection report")]
	[EndpointDescription("Finds receipts that share the same date+location, date+total, or all three fields and may be double entries.")]
	public async Task<Results<Ok<DuplicatesResponse>, BadRequestProblem>> GetDuplicates(
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
			return ApiProblem.BadRequest($"Invalid matchOn '{match}'. Allowed: dateAndLocation, dateAndTotal, dateAndLocationAndTotal");
		}

		string[] validLocTolerance = ["exact", "normalized"];
		if (!validLocTolerance.Contains(locTol.ToLowerInvariant()))
		{
			return ApiProblem.BadRequest($"Invalid locationTolerance '{locTol}'. Allowed: exact, normalized");
		}

		if (totTol < 0)
		{
			return ApiProblem.BadRequest("totalTolerance must be >= 0");
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
	public async Task<Results<Ok<AcceptDuplicateGroupResponse>, NotFoundProblem>> AcceptDuplicateGroup(
		[FromBody] AcceptDuplicateGroupRequest request,
		CancellationToken cancellationToken)
	{
		try
		{
			int acceptedPairCount = await mediator.Send(
				new AcceptDuplicateGroupCommand(DistinctReceiptIds(request)), cancellationToken);
			await notifier.NotifyAllChanged("duplicate-acceptance", "updated");
			return TypedResults.Ok(new AcceptDuplicateGroupResponse { AcceptedPairCount = acceptedPairCount });
		}
		catch (KeyNotFoundException ex)
		{
			return ApiProblem.NotFound(ex.Message);
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
		await notifier.NotifyAllChanged("duplicate-acceptance", "updated");
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
	public async Task<Results<Ok<CategoryTrendsResponse>, BadRequestProblem>> GetCategoryTrends(
		[FromQuery] DateOnly? startDate,
		[FromQuery] DateOnly? endDate,
		[FromQuery] string? granularity,
		[FromQuery] int? topN,
		CancellationToken cancellationToken,
		[FromQuery] string? categoryView = null,
		[FromQuery] string? rollup = null,
		[FromQuery] Guid? categoryId = null,
		[FromQuery] string? knowledgeCutoff = null,
		[FromQuery] Guid? capturedCurrentRevisionId = null,
		[FromQuery] Guid? envelopeId = null,
		[FromQuery] Guid? occasionId = null)
	{
		DateOnly start = startDate ?? DateOnly.MinValue;
		DateOnly end = endDate ?? DateOnly.FromDateTime(DateTime.Today);
		string gran = granularity ?? "monthly";
		int top = topN ?? 7;

		if (start > end)
		{
			return ApiProblem.BadRequest("startDate must be before or equal to endDate");
		}

		string[] validGranularities = ["daily", "monthly", "quarterly", "yearly"];
		if (!validGranularities.Contains(gran.ToLowerInvariant()))
		{
			return ApiProblem.BadRequest($"Invalid granularity '{gran}'. Allowed: daily, monthly, quarterly, yearly");
		}

		if (top < 1 || top > 50)
		{
			return ApiProblem.BadRequest("topN must be between 1 and 50");
		}

		AppReports.CategoryReportView view;
		switch (categoryView)
		{
			case null or "asClassified": view = AppReports.CategoryReportView.AsClassified; break;
			case "currentHierarchy": view = AppReports.CategoryReportView.CurrentHierarchy; break;
			default: return ApiProblem.BadRequest("categoryView must be asClassified or currentHierarchy");
		}
		AppReports.CategoryReportRollup level;
		switch (rollup)
		{
			case null or "assignedCategory": level = AppReports.CategoryReportRollup.AssignedCategory; break;
			case "root": level = AppReports.CategoryReportRollup.Root; break;
			default: return ApiProblem.BadRequest("rollup must be assignedCategory or root");
		}
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
		if (capturedCurrentRevisionId.HasValue && view != AppReports.CategoryReportView.CurrentHierarchy)
		{ return ApiProblem.BadRequest("capturedCurrentRevisionId requires currentHierarchy"); }
		GetCategoryTrendsReportQuery query = new(start, end, gran, top)
		{ CategoryOptions = new(view, level, categoryId, cutoff, capturedCurrentRevisionId, envelopeId, occasionId) };
		AppReports.CategoryTrendsResult result;
		try { result = await mediator.Send(query, cancellationToken); }
		catch (ArgumentException exception) when (exception.ParamName == "capturedCurrentRevisionId")
		{ return ApiProblem.BadRequest(exception.Message); }

		return TypedResults.Ok(new CategoryTrendsResponse
		{
			View = JsonNamingPolicy.CamelCase.ConvertName((result.Snapshot?.View ?? view).ToString()),
			Rollup = JsonNamingPolicy.CamelCase.ConvertName((result.Snapshot?.Rollup ?? level).ToString()),
			AssignmentSelection = JsonNamingPolicy.CamelCase.ConvertName((result.Snapshot?.AssignmentSelection ?? AppReports.CategoryReportAssignmentSelection.CurrentPointer).ToString()),
			CurrentRevisionId = result.Snapshot?.CurrentRevisionId,
			KnowledgeCutoff = result.Snapshot?.KnowledgeCutoff,
			Series = result.Series.Select(entry => new CategoryTrendsSeries
			{
				Currency = entry.Currency,
				IsOther = entry.IsOther,
				State = entry.IsOther ? "other" : JsonNamingPolicy.CamelCase.ConvertName(entry.Bucket!.State.ToString()),
				PathRevisionId = entry.Bucket?.PathRevisionId,
				CategoryId = entry.Bucket?.CategoryId,
				PurchaseCount = entry.IsOther ? result.Snapshot!.Buckets.Where(bucket => bucket.Currency == entry.Currency
					&& !result.Series.Any(selected => !selected.IsOther && selected.Bucket == bucket)).Sum(bucket => bucket.PurchaseCount)
					: entry.Bucket!.PurchaseCount,
				Path = entry.Bucket?.Path.Select(node => new CategoryReportPathNode
				{
					CategoryId = node.Id,
					Name = node.Name,
					Archived = node.Archived
				}).ToList() ?? []
			}).ToList(),
			CurrencyTotals = result.Snapshot?.Buckets.GroupBy(bucket => bucket.Currency).Select(group => new CategoryTrendsCurrencyTotal
			{
				Currency = group.Key,
				Amount = (double)group.Sum(bucket => bucket.Amount),
				PurchaseCount = group.Sum(bucket => bucket.PurchaseCount)
			}).ToList() ?? [],
			Categories = result.Categories,
			Buckets = result.Buckets.Select(b => new CategoryTrendsBucket
			{
				Period = b.Period,
				Amounts = b.Amounts.Select(a => (double)a).ToList()
			}).ToList()
		});
	}

	[HttpGet("budget-trends")]
	[EndpointSummary("Get spending trends by envelope or occasion")]
	public async Task<Results<Ok<BudgetTrendsResponse>, BadRequestProblem>> GetBudgetTrends(
		[FromQuery] DateOnly? startDate,
		[FromQuery] DateOnly? endDate,
		[FromQuery] string? granularity,
		[FromQuery] int? topN,
		[FromQuery] string? groupBy,
		[FromQuery] Guid? envelopeId,
		[FromQuery] Guid? occasionId,
		CancellationToken cancellationToken)
	{
		DateOnly start = startDate ?? DateOnly.MinValue;
		DateOnly end = endDate ?? DateOnly.FromDateTime(DateTime.Today);
		string gran = granularity ?? "monthly";
		int top = topN ?? 7;
		if (start > end)
		{
			return ApiProblem.BadRequest("startDate must be before or equal to endDate");
		}
		if (gran.ToLowerInvariant() is not ("daily" or "monthly" or "quarterly" or "yearly"))
		{
			return ApiProblem.BadRequest($"Invalid granularity '{gran}'. Allowed: daily, monthly, quarterly, yearly");
		}
		if (top is < 1 or > 50)
		{
			return ApiProblem.BadRequest("topN must be between 1 and 50");
		}
		AppReports.BudgetTrendsDimension dimension;
		switch (groupBy)
		{
			case null or "envelope": dimension = AppReports.BudgetTrendsDimension.Envelope; break;
			case "occasion": dimension = AppReports.BudgetTrendsDimension.Occasion; break;
			default: return ApiProblem.BadRequest("groupBy must be envelope or occasion");
		}

		AppReports.BudgetTrendsResult result = await mediator.Send(
			new GetBudgetTrendsReportQuery(start, end, gran, top, dimension, envelopeId, occasionId), cancellationToken);
		return TypedResults.Ok(new BudgetTrendsResponse
		{
			GroupBy = JsonNamingPolicy.CamelCase.ConvertName(result.Dimension.ToString()),
			Series = [.. result.Series.Select(series => new BudgetTrendsSeries
			{
				Id = series.Id,
				Name = series.Name,
				IsActive = series.IsActive,
				Currency = series.Currency,
				IsOther = series.IsOther,
				Total = (double)series.Total,
				PurchaseCount = series.PurchaseCount,
			})],
			Buckets = [.. result.Buckets.Select(bucket => new CategoryTrendsBucket
			{
				Period = bucket.Period,
				Amounts = [.. bucket.Amounts.Select(amount => (double)amount)],
			})],
		});
	}

	private static CategoryAssistanceCandidateRead Candidate(Application.Classification.CategoryAssistanceCandidateRead candidate)
		=> new() { CategoryId = candidate.CategoryId, Probability = candidate.Probability };

	[HttpGet("uncategorized-items")]
	[EndpointSummary("Get uncategorized items report")]
	[EndpointDescription("Returns active receipt items whose current classification is missing, unclassified, or unresolved.")]
	public async Task<Results<Ok<UncategorizedItemsResponse>, BadRequestProblem>> GetUncategorizedItems(
		[FromQuery] string? sortBy,
		[FromQuery] string? sortDirection,
		[FromQuery] int? page,
		[FromQuery] int? pageSize,
		[FromQuery] string? suggestion,
		CancellationToken cancellationToken)
	{
		string sort = sortBy ?? "description";
		string suggestionFilter = suggestion ?? "any";
		string direction = sortDirection ?? "asc";
		int pg = page ?? 1;
		int ps = pageSize ?? 50;

		string[] validSortColumns = ["description", "total", "itemcode"];
		if (!validSortColumns.Contains(sort.ToLowerInvariant()))
		{
			return ApiProblem.BadRequest($"Invalid sortBy '{sort}'. Allowed: description, total, itemCode");
		}

		string[] validDirections = ["asc", "desc"];
		if (!validDirections.Contains(direction.ToLowerInvariant()))
		{
			return ApiProblem.BadRequest($"Invalid sortDirection '{direction}'. Allowed: asc, desc");
		}

		if (pg < 1)
		{
			return ApiProblem.BadRequest("page must be at least 1");
		}

		if (ps < 1 || ps > 100)
		{
			return ApiProblem.BadRequest("pageSize must be between 1 and 100");
		}

		string[] validSuggestionFilters = ["any", "with", "without"];
		if (!validSuggestionFilters.Contains(suggestionFilter.ToLowerInvariant()))
		{
			return ApiProblem.BadRequest($"Invalid suggestion '{suggestionFilter}'. Allowed: any, with, without");
		}

		GetUncategorizedItemsReportQuery query = new(sort, direction, pg, ps, suggestionFilter);
		AppReports.UncategorizedItemsResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new UncategorizedItemsResponse
		{
			TotalCount = result.TotalCount,
			SuggestionsAvailable = result.SuggestionsAvailable,
			Items = result.Items.Select(i => new UncategorizedItem
			{
				Id = i.Id,
				ReceiptId = i.ReceiptId,
				ReceiptItemCode = i.ReceiptItemCode,
				Description = i.Description,
				Quantity = (double)i.Quantity,
				UnitPrice = (double)i.UnitPrice,
				TotalAmount = (double)i.TotalAmount,
				// Retired legacy label; the contract keeps the field.
				Category = "",
				ClassificationState = i.ClassificationState,
				AssignmentId = i.AssignmentId,
				CategoryId = i.CategoryId,
				TaxonomyRevisionId = i.TaxonomyRevisionId,
				TotalAmountCurrency = i.TotalAmountCurrency,
				Location = i.Location,
				Suggestion = i.Suggestion is null ? null : new UncategorizedItemSuggestion
				{
					AttemptId = i.Suggestion.AttemptId,
					State = i.Suggestion.State,
					CategoryId = i.Suggestion.CategoryId,
					Confidence = i.Suggestion.Confidence,
					AbstentionReason = i.Suggestion.AbstentionReason,
					RootCandidates = [.. i.Suggestion.RootCandidates.Select(Candidate)],
					LeafCandidates = [.. i.Suggestion.LeafCandidates.Select(Candidate)],
				}
			}).ToList()
		});
	}

	[HttpGet("spending-by-normalized-description")]
	[EndpointSummary("Get spending by normalized description report")]
	[EndpointDescription("Aggregates receipt item spending grouped by normalized description canonical name. Items without a normalized description bucket into a synthetic \"(Not Normalized)\" group.")]
	public async Task<Results<Ok<SpendingByNormalizedDescriptionResponse>, BadRequestProblem>> GetSpendingByNormalizedDescription(
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
			return ApiProblem.BadRequest($"Invalid sortBy '{sort}'. Allowed: canonicalName, totalAmount, itemCount");
		}

		string[] validDirections = ["asc", "desc"];
		if (!validDirections.Contains(direction.ToLowerInvariant()))
		{
			return ApiProblem.BadRequest($"Invalid sortDirection '{direction}'. Allowed: asc, desc");
		}

		if (pg < 1)
		{
			return ApiProblem.BadRequest("page must be at least 1");
		}

		if (ps < 1 || ps > 100)
		{
			return ApiProblem.BadRequest("pageSize must be between 1 and 100");
		}

		if (from.HasValue && to.HasValue && from.Value > to.Value)
		{
			return ApiProblem.BadRequest("from must be before or equal to to");
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
				NormalizedDescriptionId = i.NormalizedDescriptionId,
				NormalizationReviewState = i.NormalizationReviewState,
				TotalAmount = (double)i.TotalAmount,
				Currency = i.Currency,
				ItemCount = i.ItemCount,
				FirstSeen = i.FirstSeen,
				LastSeen = i.LastSeen,
				Status = i.Status switch
				{
					Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Active => NormalizedDescriptionStatus.Active,
					Domain.NormalizedDescriptions.NormalizedDescriptionStatus.PendingReview => NormalizedDescriptionStatus.PendingReview,
					_ => null
				}
			}).ToList()
		});
	}
}
