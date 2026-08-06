using API.Generated.Dtos;
using Application.Models.Dashboard;
using Application.Queries.Aggregates.Dashboard;
using Asp.Versioning;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers.Aggregates;

[ApiVersion("1.0")]
[ApiController]
[Route("api/dashboard")]
[Produces("application/json")]
[Authorize]
public class DashboardController(IMediator mediator) : ControllerBase
{
	[HttpGet("summary")]
	[EndpointSummary("Get dashboard summary statistics")]
	[EndpointDescription("Returns high-level stats for a date range including total receipts, total spent, average trip amount, and most-used account/category.")]
	public async Task<Results<Ok<DashboardSummaryResponse>, BadRequest<ProblemDetails>>> GetDashboardSummary(
		[FromQuery] DateOnly? startDate,
		[FromQuery] DateOnly? endDate,
		CancellationToken cancellationToken)
	{
		DateOnly start = startDate ?? DateOnly.MinValue;
		DateOnly end = endDate ?? DateOnly.FromDateTime(DateTime.Today);

		if (start > end)
		{
			return ApiProblem.BadRequest("startDate must be before or equal to endDate");
		}

		GetDashboardSummaryQuery query = new(start, end);
		DashboardSummaryResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new DashboardSummaryResponse
		{
			TotalReceipts = result.TotalReceipts,
			TotalSpent = (double)result.TotalSpent,
			AverageTripAmount = (double)result.AverageTripAmount,
			MostUsedAccount = new NameCountPair
			{
				Name = result.MostUsedAccount.Name,
				Count = result.MostUsedAccount.Count
			},
			MostUsedCategory = new NameCountPair
			{
				Name = result.MostUsedCategory.Name,
				Count = result.MostUsedCategory.Count
			}
		});
	}

	[HttpGet("spending-over-time")]
	[EndpointSummary("Get spending over time")]
	[EndpointDescription("Returns time-series spending data bucketed by the specified granularity.")]
	public async Task<Results<Ok<SpendingOverTimeResponse>, BadRequest<ProblemDetails>>> GetSpendingOverTime(
		[FromQuery] DateOnly? startDate,
		[FromQuery] DateOnly? endDate,
		[FromQuery] string? granularity,
		CancellationToken cancellationToken)
	{
		DateOnly start = startDate ?? DateOnly.MinValue;
		DateOnly end = endDate ?? DateOnly.FromDateTime(DateTime.Today);
		string gran = granularity ?? "monthly";

		if (start > end)
		{
			return ApiProblem.BadRequest("startDate must be before or equal to endDate");
		}

		string[] validGranularities = ["daily", "monthly", "quarterly", "yearly"];
		if (!validGranularities.Contains(gran.ToLowerInvariant()))
		{
			return ApiProblem.BadRequest($"Invalid granularity '{gran}'. Allowed: daily, monthly, quarterly, yearly");
		}

		GetSpendingOverTimeQuery query = new(start, end, gran);
		SpendingOverTimeResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new SpendingOverTimeResponse
		{
			Buckets = result.Buckets.Select(b => new SpendingBucket
			{
				Period = b.Period,
				Amount = (double)b.Amount
			}).ToList()
		});
	}

	[HttpGet("earliest-receipt-year")]
	[EndpointSummary("Get the earliest receipt year")]
	[EndpointDescription("Returns the year of the oldest receipt in the system. Used for populating year dropdowns.")]
	public async Task<Ok<EarliestReceiptYearResponse>> GetEarliestReceiptYear(
		CancellationToken cancellationToken)
	{
		GetEarliestReceiptYearQuery query = new();
		int year = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new EarliestReceiptYearResponse
		{
			Year = year
		});
	}

	[HttpGet("spending-by-category")]
	[EndpointSummary("Get spending grouped by category")]
	[EndpointDescription("Returns spending amounts grouped by receipt item category.")]
	public async Task<Results<Ok<SpendingByCategoryResponse>, BadRequest<ProblemDetails>>> GetSpendingByCategory(
		[FromQuery] DateOnly? startDate,
		[FromQuery] DateOnly? endDate,
		[FromQuery] int limit = 10,
		CancellationToken cancellationToken = default)
	{
		DateOnly start = startDate ?? DateOnly.MinValue;
		DateOnly end = endDate ?? DateOnly.FromDateTime(DateTime.Today);

		if (start > end)
		{
			return ApiProblem.BadRequest("startDate must be before or equal to endDate");
		}

		if (limit <= 0 || limit > 100)
		{
			return ApiProblem.BadRequest("limit must be between 1 and 100");
		}

		GetSpendingByCategoryQuery query = new(start, end, limit);
		SpendingByCategoryResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new SpendingByCategoryResponse
		{
			Items = result.Items.Select(i => new SpendingCategoryItem
			{
				CategoryName = i.CategoryName,
				Amount = (double)i.Amount,
				Percentage = (double)i.Percentage
			}).ToList()
		});
	}

	[HttpGet("spending-by-store")]
	[EndpointSummary("Get spending grouped by store")]
	[EndpointDescription("Returns spending amounts grouped by receipt location (store), including visit count and average per visit.")]
	public async Task<Results<Ok<SpendingByStoreResponse>, BadRequest<ProblemDetails>>> GetSpendingByStore(
		[FromQuery] DateOnly? startDate,
		[FromQuery] DateOnly? endDate,
		CancellationToken cancellationToken)
	{
		DateOnly start = startDate ?? DateOnly.MinValue;
		DateOnly end = endDate ?? DateOnly.FromDateTime(DateTime.Today);

		if (start > end)
		{
			return ApiProblem.BadRequest("startDate must be before or equal to endDate");
		}

		GetSpendingByStoreQuery query = new(start, end);
		SpendingByStoreResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new SpendingByStoreResponse
		{
			Items = result.Items.Select(i => new SpendingByStoreItem
			{
				Location = i.Location,
				VisitCount = i.VisitCount,
				TotalAmount = (double)i.TotalAmount,
				AveragePerVisit = (double)i.AveragePerVisit
			}).ToList()
		});
	}

	[HttpGet("spending-by-account")]
	[EndpointSummary("Get spending grouped by account")]
	[EndpointDescription("Returns spending amounts grouped by payment account.")]
	public async Task<Results<Ok<SpendingByAccountResponse>, BadRequest<ProblemDetails>>> GetSpendingByAccount(
		[FromQuery] DateOnly? startDate,
		[FromQuery] DateOnly? endDate,
		CancellationToken cancellationToken)
	{
		DateOnly start = startDate ?? DateOnly.MinValue;
		DateOnly end = endDate ?? DateOnly.FromDateTime(DateTime.Today);

		if (start > end)
		{
			return ApiProblem.BadRequest("startDate must be before or equal to endDate");
		}

		GetSpendingByAccountQuery query = new(start, end);
		SpendingByAccountResult result = await mediator.Send(query, cancellationToken);

		return TypedResults.Ok(new SpendingByAccountResponse
		{
			Items = result.Items.Select(i => new SpendingAccountItem
			{
				AccountId = i.AccountId,
				AccountName = i.AccountName,
				Amount = (double)i.Amount,
				Percentage = (double)i.Percentage
			}).ToList()
		});
	}
}
