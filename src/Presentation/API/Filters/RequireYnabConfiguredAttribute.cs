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

	private class RequireYnabConfiguredFilter(IYnabCredentialSource credentialSource, ILogger<RequireYnabConfiguredAttribute> logger) : IAsyncActionFilter
	{
		public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
		{
			if (!(await credentialSource.GetStateAsync(context.HttpContext.RequestAborted)).IsUsable)
			{
				logger.LogWarning("YNAB is not connected — no OAuth connection and no personal access token");
				context.Result = new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
				return;
			}

			await next();
		}
	}
}
