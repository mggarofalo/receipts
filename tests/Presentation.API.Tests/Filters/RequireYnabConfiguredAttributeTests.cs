using API.Filters;
using Application.Interfaces.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Presentation.API.Tests.Filters;

public class RequireYnabConfiguredAttributeTests
{
	private readonly Mock<IYnabApiClient> _ynabClientMock = new();
	private readonly Mock<ILogger<RequireYnabConfiguredAttribute>> _loggerMock = new();

	private ActionExecutingContext CreateContext()
	{
		DefaultHttpContext httpContext = new();
		ActionContext actionContext = new(httpContext, new RouteData(), new ActionDescriptor());
		return new ActionExecutingContext(
			actionContext,
			[],
			new Dictionary<string, object?>(),
			new object());
	}

	private IAsyncActionFilter CreateFilter()
	{
		ServiceCollection services = new();
		services.AddSingleton(_ynabClientMock.Object);
		services.AddSingleton(_loggerMock.Object);
		ServiceProvider provider = services.BuildServiceProvider();

		RequireYnabConfiguredAttribute attribute = new();
		IFilterFactory factory = (IFilterFactory)attribute;
		return (IAsyncActionFilter)factory.CreateInstance(provider);
	}

	[Fact]
	public async Task OnActionExecutionAsync_WhenConfigured_CallsNext()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(true);
		IAsyncActionFilter filter = CreateFilter();
		ActionExecutingContext context = CreateContext();
		bool nextCalled = false;

		// Act
		await filter.OnActionExecutionAsync(context, () =>
		{
			nextCalled = true;
			return Task.FromResult<ActionExecutedContext>(null!);
		});

		// Assert
		nextCalled.Should().BeTrue();
		context.Result.Should().BeNull();
	}

	[Fact]
	public async Task OnActionExecutionAsync_WhenNotConfigured_Returns503()
	{
		// Arrange
		_ynabClientMock.Setup(c => c.IsConfigured).Returns(false);
		IAsyncActionFilter filter = CreateFilter();
		ActionExecutingContext context = CreateContext();
		bool nextCalled = false;

		// Act
		await filter.OnActionExecutionAsync(context, () =>
		{
			nextCalled = true;
			return Task.FromResult<ActionExecutedContext>(null!);
		});

		// Assert
		nextCalled.Should().BeFalse();
		StatusCodeResult statusCodeResult = Assert.IsType<StatusCodeResult>(context.Result);
		statusCodeResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
	}
}
