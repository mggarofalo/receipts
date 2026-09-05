using API.Filters;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.API.Tests.Filters;

public class CollectionValidationTests
{
	private sealed record Item(string Name);
	private sealed class ItemValidator : AbstractValidator<Item>
	{
		public ItemValidator() => RuleFor(item => item.Name).NotEmpty();
	}
	private sealed class RootValidator(Action validated) : AbstractValidator<List<Item>>
	{
		public override Task<FluentValidation.Results.ValidationResult> ValidateAsync(ValidationContext<List<Item>> context, CancellationToken cancellation = default)
		{
			validated();
			return base.ValidateAsync(context, cancellation);
		}
	}
	private static ActionExecutingContext Context(object body, CancellationToken cancellation = default) => new(
		new ActionContext(new DefaultHttpContext { RequestAborted = cancellation }, new RouteData(), new ActionDescriptor()), [],
		new Dictionary<string, object?> { ["body"] = body }, new object());

	[Fact]
	public async Task RootAndElementValidators_BothRun_AndInvalidLaterElementPreventsAction()
	{
		bool rootValidated = false;
		ServiceCollection services = new();
		services.AddSingleton<IValidator<Item>, ItemValidator>();
		services.AddSingleton<IValidator<List<Item>>>(new RootValidator(() => rootValidated = true));
		using ServiceProvider provider = services.BuildServiceProvider();
		bool actionCalled = false;
		Func<Task> act = () => new FluentValidationActionFilter(provider).OnActionExecutionAsync(Context(new List<Item> { new("valid"), new("") }), () =>
		{
			actionCalled = true;
			return Task.FromResult<ActionExecutedContext>(null!);
		});

		ValidationException exception = (await act.Should().ThrowAsync<ValidationException>()).Which;
		exception.Errors.Should().Contain(e => e.PropertyName == "[1].Name");
		rootValidated.Should().BeTrue();
		actionCalled.Should().BeFalse();
	}

	[Fact]
	public async Task RootFailure_DoesNotHideElementFailures()
	{
		ServiceCollection services = new();
		RootValidator root = new(() => { });
		root.RuleFor(items => items.Count).GreaterThan(2);
		services.AddSingleton<IValidator<List<Item>>>(root);
		services.AddSingleton<IValidator<Item>, ItemValidator>();
		using ServiceProvider provider = services.BuildServiceProvider();
		Func<Task> act = () => new FluentValidationActionFilter(provider).OnActionExecutionAsync(Context(new List<Item> { new("") }),
			() => throw new InvalidOperationException("Action must not run"));

		ValidationException exception = (await act.Should().ThrowAsync<ValidationException>()).Which;

		exception.Errors.Select(error => error.PropertyName).Should().BeEquivalentTo(["Count", "[0].Name"]);
	}

	[Fact]
	public async Task EveryInvalidElement_HasAnIndexedFailure()
	{
		ServiceCollection services = new();
		services.AddSingleton<IValidator<Item>, ItemValidator>();
		using ServiceProvider provider = services.BuildServiceProvider();
		Func<Task> act = () => new FluentValidationActionFilter(provider).OnActionExecutionAsync(Context(new List<Item> { new(""), new("valid"), new("") }),
			() => throw new InvalidOperationException("Action must not run"));
		ValidationException exception = (await act.Should().ThrowAsync<ValidationException>()).Which;
		exception.Errors.Select(e => e.PropertyName).Should().BeEquivalentTo(["[0].Name", "[2].Name"]);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task EmptyCollectionOrNullMember_IsRejected(bool nullMember)
	{
		using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
		List<Item?> items = nullMember ? [null] : [];
		Func<Task> act = () => new FluentValidationActionFilter(provider).OnActionExecutionAsync(Context(items),
			() => throw new InvalidOperationException("Action must not run"));
		ValidationException exception = (await act.Should().ThrowAsync<ValidationException>()).Which;
		exception.Errors.Should().Contain(e => e.PropertyName == (nullMember ? "[0]" : ""));
	}

	[Fact]
	public async Task ValidCollection_InvokesActionOnlyAfterEveryAsyncValidatorCompletes()
	{
		List<string> events = [];
		InlineValidator<Item> validator = new();
		validator.RuleFor(item => item.Name).MustAsync(async (name, _) =>
		{
			await Task.Yield();
			events.Add(name);
			return true;
		});
		ServiceCollection services = new();
		services.AddSingleton<IValidator<Item>>(validator);
		using ServiceProvider provider = services.BuildServiceProvider();
		await new FluentValidationActionFilter(provider).OnActionExecutionAsync(Context(new List<Item> { new("first"), new("second") }), () =>
		{
			events.Should().BeEquivalentTo(["first", "second"]);
			events.Add("action");
			return Task.FromResult<ActionExecutedContext>(null!);
		});
		events.Should().HaveCount(3);
		events.Last().Should().Be("action");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RequestCancellation_ReachesAsyncElementValidator_AndStopsAction(bool validatorThrows)
	{
		using CancellationTokenSource cancellation = new();
		InlineValidator<Item> validator = new();
		validator.RuleFor(item => item.Name).MustAsync((_, token) =>
		{
			token.Should().Be(cancellation.Token);
			cancellation.Cancel();
			if (validatorThrows)
			{
				token.ThrowIfCancellationRequested();
			}

			return Task.FromResult(true);
		});
		ServiceCollection services = new();
		services.AddSingleton<IValidator<Item>>(validator);
		using ServiceProvider provider = services.BuildServiceProvider();
		Func<Task> act = () => new FluentValidationActionFilter(provider).OnActionExecutionAsync(Context(new List<Item> { new("valid") }, cancellation.Token),
			() => throw new InvalidOperationException("Action must not run"));
		await act.Should().ThrowAsync<OperationCanceledException>();
	}
}
