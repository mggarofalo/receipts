using Application.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace API.Filters;

[AttributeUsage(AttributeTargets.Method)]
public class RequireYnabConfiguredAttribute : TypeFilterAttribute
{
	public RequireYnabConfiguredAttribute() : base(typeof(RequireYnabConfiguredFilter))
	{
	}

	private class RequireYnabConfiguredFilter(IYnabApiClient ynabClient, ILogger<RequireYnabConfiguredAttribute> logger) : IAsyncActionFilter
	{
		public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
		{
			if (!ynabClient.IsConfigured)
			{
				logger.LogWarning("YNAB API client is not configured — missing personal access token");
				context.Result = new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
				return;
			}

			await next();
		}
	}
}
