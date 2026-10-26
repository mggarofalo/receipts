using Application.Classification;
using Infrastructure.Jev;
using Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.Tests.Services;

public sealed class CategoryAssistanceWorkerTests
{
	private static readonly CategoryAssistanceSettings EnabledSettings =
			new(true, "jev", CategorySuggestionPolicy.Model, 10, 3, Guid.NewGuid());

	[Theory]
	[InlineData(false, true, "key")]
	[InlineData(true, false, "key")]
	[InlineData(true, true, null)]
	public async Task NoConsentOrTransportDoesNotReconcileOrClaim(
			bool consent, bool serverEnabled, string? key)
	{
		var settings = new Mock<ICategoryAssistanceSettingsStore>();
		settings.Setup(x => x.GetAsync(It.IsAny<CancellationToken>()))
				.ReturnsAsync(EnabledSettings with { Enabled = consent });
		var reconciler = new Mock<ICategoryAssistanceConnectionReconciler>(MockBehavior.Strict);
		var store = new Mock<ICategoryAssistanceDispatchStore>(MockBehavior.Strict);
		using ServiceProvider services = Services(settings.Object, reconciler.Object, store.Object,
				new JevOptions { Enabled = serverEnabled, ApiKey = key });
		var worker = new CategoryAssistanceWorker(services.GetRequiredService<IServiceScopeFactory>(),
				services.GetRequiredService<IOptionsMonitor<JevOptions>>(),
				NullLogger<CategoryAssistanceWorker>.Instance);

		Assert.False(await worker.RunOnceAsync());
		reconciler.VerifyNoOtherCalls();
		store.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task EnabledPassReconcilesBeforeClaimingOnlyOneQueuedItem()
	{
		var settings = new Mock<ICategoryAssistanceSettingsStore>();
		settings.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(EnabledSettings);
		var reconciler = new Mock<ICategoryAssistanceConnectionReconciler>(MockBehavior.Strict);
		var store = new Mock<ICategoryAssistanceDispatchStore>(MockBehavior.Strict);
		var order = new MockSequence();
		reconciler.InSequence(order).Setup(x => x.ReconcileAsync(
				It.Is<CategoryAssistanceConnectionCoordinator.Configuration>(c =>
						c.Credential == "key" && c.Provider == "jev"),
				It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
		store.InSequence(order).Setup(x => x.ClaimQueuedAsync(It.IsAny<CancellationToken>()))
				.ReturnsAsync((CategoryAssistanceQueuedWork?)null);
		using ServiceProvider services = Services(settings.Object, reconciler.Object, store.Object,
				new JevOptions { Enabled = true, ApiKey = "key" });
		var worker = new CategoryAssistanceWorker(services.GetRequiredService<IServiceScopeFactory>(),
				services.GetRequiredService<IOptionsMonitor<JevOptions>>(),
				NullLogger<CategoryAssistanceWorker>.Instance);

		Assert.False(await worker.RunOnceAsync());
		reconciler.VerifyAll();
		store.VerifyAll();
	}

	private static ServiceProvider Services(ICategoryAssistanceSettingsStore settings,
			ICategoryAssistanceConnectionReconciler reconciler, ICategoryAssistanceDispatchStore store,
			JevOptions options)
	{
		ServiceCollection services = new();
		var monitor = new Mock<IOptionsMonitor<JevOptions>>();
		monitor.SetupGet(x => x.CurrentValue).Returns(options);
		services.AddSingleton(monitor.Object);
		services.AddSingleton(settings);
		services.AddSingleton(reconciler);
		services.AddSingleton(store);
		services.AddSingleton<TimeProvider>(TimeProvider.System);
		services.AddSingleton<ICategoryAssistanceCapabilitySource>(Mock.Of<ICategoryAssistanceCapabilitySource>());
		services.AddSingleton<ICategoryAssistanceSelectedProvider>(Mock.Of<ICategoryAssistanceSelectedProvider>());
		services.AddScoped<CategoryAssistanceDispatcher>();
		return services.BuildServiceProvider();
	}
}
