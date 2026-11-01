using API.Filters;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.API.Tests.Filters;

public class FluentValidationActionFilterTests
{
	public record TestModel(string Name);

	public class PassingValidator : AbstractValidator<TestModel>
	{
	}

	public class FailingValidator : AbstractValidator<TestModel>
	{
		public FailingValidator()
		{
			RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required");
		}
	}

	private static ActionExecutingContext CreateContext(Dictionary<string, object?> arguments)
	{
		DefaultHttpContext httpContext = new();
		ActionContext actionContext = new(httpContext, new RouteData(), new ActionDescriptor());
		return new ActionExecutingContext(
			actionContext,
			[],
			arguments,
			new object());
	}

	[Fact]
	public async Task OnActionExecutionAsync_NoValidator_CallsNext()
	{
		// Arrange
		ServiceCollection services = new();
		ServiceProvider provider = services.BuildServiceProvider();
		FluentValidationActionFilter filter = new(provider);
		ActionExecutingContext context = CreateContext(new Dictionary<string, object?>
		{
			["model"] = new TestModel("test")
		});
		bool nextCalled = false;

		// Act
		await filter.OnActionExecutionAsync(context, () =>
		{
			nextCalled = true;
			return Task.FromResult<ActionExecutedContext>(null!);
		});

		// Assert
		nextCalled.Should().BeTrue();
	}

	[Fact]
	public async Task OnActionExecutionAsync_ValidModel_CallsNext()
	{
		// Arrange
		ServiceCollection services = new();
		services.AddSingleton<IValidator<TestModel>, PassingValidator>();
		ServiceProvider provider = services.BuildServiceProvider();

		FluentValidationActionFilter filter = new(provider);
		ActionExecutingContext context = CreateContext(new Dictionary<string, object?>
		{
			["model"] = new TestModel("test")
		});
		bool nextCalled = false;

		// Act
		await filter.OnActionExecutionAsync(context, () =>
		{
			nextCalled = true;
			return Task.FromResult<ActionExecutedContext>(null!);
		});

		// Assert
		nextCalled.Should().BeTrue();
	}

	[Fact]
	public async Task OnActionExecutionAsync_InvalidModel_ThrowsValidationException()
	{
		// Arrange
		ServiceCollection services = new();
		services.AddSingleton<IValidator<TestModel>, FailingValidator>();
		ServiceProvider provider = services.BuildServiceProvider();

		FluentValidationActionFilter filter = new(provider);
		ActionExecutingContext context = CreateContext(new Dictionary<string, object?>
		{
			["model"] = new TestModel("")
		});

		// Act
		Func<Task> act = () => filter.OnActionExecutionAsync(context, () =>
			Task.FromResult<ActionExecutedContext>(null!));

		// Assert
		await act.Should().ThrowAsync<ValidationException>();
	}

	[Fact]
	public async Task OnActionExecutionAsync_EmptyList_ThrowsValidationException()
	{
		// Arrange
		ServiceCollection services = new();
		ServiceProvider provider = services.BuildServiceProvider();
		FluentValidationActionFilter filter = new(provider);
		ActionExecutingContext context = CreateContext(new Dictionary<string, object?>
		{
			["model"] = new List<TestModel>()
		});

		// Act
		Func<Task> act = () => filter.OnActionExecutionAsync(context, () =>
			Task.FromResult<ActionExecutedContext>(null!));

		// Assert
		await act.Should().ThrowAsync<ValidationException>();
	}

	[Fact]
	public async Task OnActionExecutionAsync_NullArgument_SkipsValidation()
	{
		// Arrange
		ServiceCollection services = new();
		ServiceProvider provider = services.BuildServiceProvider();
		FluentValidationActionFilter filter = new(provider);
		ActionExecutingContext context = CreateContext(new Dictionary<string, object?>
		{
			["model"] = null
		});
		bool nextCalled = false;

		// Act
		await filter.OnActionExecutionAsync(context, () =>
		{
			nextCalled = true;
			return Task.FromResult<ActionExecutedContext>(null!);
		});

		// Assert
		nextCalled.Should().BeTrue();
	}

	private static ActionExecutingContext CreateBoundContext(string name, object argument, BindingSource source)
	{
		ActionDescriptor descriptor = new()
		{
			Parameters = [new ParameterDescriptor { Name = name, ParameterType = argument.GetType(), BindingInfo = new BindingInfo { BindingSource = source } }],
		};
		return new ActionExecutingContext(
			new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor),
			[],
			new Dictionary<string, object?> { [name] = argument },
			new object());
	}

	[Fact]
	public async Task OnActionExecutionAsync_EmptyQueryArray_CallsNext()
	{
		// Arrange
		using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
		FluentValidationActionFilter filter = new(provider);
		ActionExecutingContext context = CreateBoundContext("status", Array.Empty<string>(), BindingSource.Query);
		bool nextCalled = false;

		// Act
		await filter.OnActionExecutionAsync(context, () =>
		{
			nextCalled = true;
			return Task.FromResult<ActionExecutedContext>(null!);
		});

		// Assert
		nextCalled.Should().BeTrue();
	}

	[Fact]
	public async Task OnActionExecutionAsync_EmptyBodyList_ThrowsValidationException()
	{
		// Arrange
		using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
		FluentValidationActionFilter filter = new(provider);
		ActionExecutingContext context = CreateBoundContext("model", new List<TestModel>(), BindingSource.Body);

		// Act
		Func<Task> act = () => filter.OnActionExecutionAsync(context, () =>
			Task.FromResult<ActionExecutedContext>(null!));

		// Assert
		await act.Should().ThrowAsync<ValidationException>();
	}
}
