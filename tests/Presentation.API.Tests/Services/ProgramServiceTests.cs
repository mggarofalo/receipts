using System.Security.Claims;
using API.Mapping.Aggregates;
using API.Mapping.Core;
using API.Services;
using Application.Interfaces.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Presentation.API.Tests.Services;

public class ProgramServiceTests
{
	[Fact]
	public void RegisterProgramServices_RegistersRequiredServices()
	{
		ServiceCollection serviceCollection = new();
		serviceCollection.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
		serviceCollection.RegisterProgramServices();
		ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();

		// Verify that all Mapperly mappers are registered
		AssertThatMappersAreRegistered(serviceProvider);
	}

	[Fact]
	public void RegisterProgramServices_RegistersCurrentUserAccessorAsSingleton()
	{
		// RECEIPTS-753: the accessor must be a singleton so the singleton IDbContextFactory can inject it
		// into factory-created contexts without a captive-dependency violation.
		ServiceCollection serviceCollection = new();
		serviceCollection.RegisterProgramServices();

		ServiceDescriptor descriptor = serviceCollection.Single(d => d.ServiceType == typeof(ICurrentUserAccessor));

		descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
		descriptor.ImplementationType.Should().Be<CurrentUserAccessor>();
	}

	[Fact]
	public void CurrentUserAccessor_SingletonInstance_ReadsCurrentRequestLazily()
	{
		// The singleton facade captures nothing at construction; it reads IHttpContextAccessor.HttpContext
		// on each property access. So a single shared instance still reflects whichever request is ambient.
		ServiceCollection serviceCollection = new();
		serviceCollection.RegisterProgramServices();
		ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();

		IHttpContextAccessor httpContextAccessor = serviceProvider.GetRequiredService<IHttpContextAccessor>();
		ICurrentUserAccessor accessor = serviceProvider.GetRequiredService<ICurrentUserAccessor>();

		// No ambient request yet.
		accessor.UserId.Should().BeNull();

		// Attach a request with an authenticated user; the same singleton instance must observe it.
		DefaultHttpContext firstRequest = new()
		{
			User = new ClaimsPrincipal(new ClaimsIdentity(
				[new Claim(ClaimTypes.NameIdentifier, "user-a")], "test")),
		};
		httpContextAccessor.HttpContext = firstRequest;
		accessor.UserId.Should().Be("user-a");

		// Swap in a different ambient request; the read is lazy, so it tracks the change.
		DefaultHttpContext secondRequest = new()
		{
			User = new ClaimsPrincipal(new ClaimsIdentity(
				[new Claim(ClaimTypes.NameIdentifier, "user-b")], "test")),
		};
		httpContextAccessor.HttpContext = secondRequest;
		accessor.UserId.Should().Be("user-b");
	}

	private static void AssertThatMappersAreRegistered(ServiceProvider serviceProvider)
	{
		// Core mappers
		Assert.NotNull(serviceProvider.GetService<CardMapper>());
		Assert.NotNull(serviceProvider.GetService<ReceiptMapper>());
		Assert.NotNull(serviceProvider.GetService<ReceiptItemMapper>());
		Assert.NotNull(serviceProvider.GetService<TransactionMapper>());

		// Aggregate mappers
		Assert.NotNull(serviceProvider.GetService<ReceiptWithItemsMapper>());
		Assert.NotNull(serviceProvider.GetService<TransactionAccountMapper>());
		Assert.NotNull(serviceProvider.GetService<TripMapper>());
	}
}
