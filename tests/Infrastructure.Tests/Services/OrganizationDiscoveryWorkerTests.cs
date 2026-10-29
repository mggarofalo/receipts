using Application.Classification;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Infrastructure.Tests.Services;

public sealed class OrganizationDiscoveryWorkerTests
{
	[Fact]
	public async Task HostedPassDeadlineCancelsBlockedSettingsRead()
	{
		var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var settings = new Mock<ICategoryAssistanceSettingsStore>();
		settings.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).Returns(async (CancellationToken ct) =>
		{
			try { await Task.Delay(Timeout.Infinite, ct); }
			catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
			return CategoryAssistanceSettings.Default;
		});
		using var services = new ServiceCollection().AddSingleton(settings.Object).BuildServiceProvider();
		using var worker = new OrganizationDiscoveryWorker(services.GetRequiredService<IServiceScopeFactory>(), Mock.Of<ILogger<OrganizationDiscoveryWorker>>());
		await worker.StartAsync(default);
		try { await canceled.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
		finally { await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3)); }
	}

	[Theory]
	[InlineData(false, "jev", "synthetic", 10)]
	[InlineData(true, null, "synthetic", 10)]
	[InlineData(true, "jev", null, 10)]
	[InlineData(true, "jev", "synthetic", 0)]
	public async Task OffOrInvalidSettingsNeverResolveDiscoveryStore(bool enabled, string? provider, string? model, int limit)
	{
		var settings = new Mock<ICategoryAssistanceSettingsStore>();
		settings.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CategoryAssistanceSettings(enabled, provider, model, limit, 3, Guid.NewGuid()));
		using var services = new ServiceCollection().AddSingleton(settings.Object).BuildServiceProvider();
		using var worker = new OrganizationDiscoveryWorker(services.GetRequiredService<IServiceScopeFactory>(), Mock.Of<ILogger<OrganizationDiscoveryWorker>>());
		(await worker.RunOnceAsync()).Should().Be(0);
	}

	[Fact]
	public async Task ShutdownCancelsPendingPassAndFailureLogContainsOnlyExceptionType()
	{
		const string sensitive = "synthetic-secret-description-and-credential";
		var settings = new Mock<ICategoryAssistanceSettingsStore>();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int calls = 0;
		settings.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).Returns(async (CancellationToken ct) =>
		{
			if (Interlocked.Increment(ref calls) == 1) { throw new InvalidOperationException(sensitive); }
			entered.TrySetResult();
			await Task.Delay(Timeout.Infinite, ct);
			return CategoryAssistanceSettings.Default;
		});
		var logger = new Mock<ILogger<OrganizationDiscoveryWorker>>();
		var logs = new List<string>();
		logger.Setup(x => x.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
			.Callback(new InvocationAction(invocation =>
			{
				logs.Add(invocation.Arguments[2].ToString()!);
				invocation.Arguments[3].Should().BeNull("exception messages can contain private source data");
			}));
		using var services = new ServiceCollection().AddSingleton(settings.Object).BuildServiceProvider();
		using var worker = new OrganizationDiscoveryWorker(services.GetRequiredService<IServiceScopeFactory>(), logger.Object);
		await worker.StartAsync(default);
		try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
		finally { await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3)); }
		logs.Should().ContainSingle().Which.Should().Contain(nameof(InvalidOperationException)).And.NotContain(sensitive);
	}
}
