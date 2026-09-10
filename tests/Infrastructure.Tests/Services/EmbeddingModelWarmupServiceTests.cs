using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Infrastructure.Tests.Services;

public class EmbeddingModelWarmupServiceTests
{
	[Fact]
	public async Task StartAsync_ProvisionedUnloadedModel_WarmsExplicitly()
	{
		Mock<IEmbeddingModelRuntime> runtime = new();
		runtime.SetupGet(candidate => candidate.IsProvisioned).Returns(true);
		runtime.SetupGet(candidate => candidate.IsLoaded).Returns(false);
		runtime.SetupGet(candidate => candidate.IsReady).Returns(false);
		runtime.Setup(candidate => candidate.WarmUpAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		EmbeddingModelWarmupService service = new(
			runtime.Object,
			NullLogger<EmbeddingModelWarmupService>.Instance);

		await service.StartAsync(CancellationToken.None);
		await WaitForAsync(() => runtime.Invocations.Any(
			invocation => invocation.Method.Name == nameof(IEmbeddingModelRuntime.WarmUpAsync)));
		await service.StopAsync(CancellationToken.None);

		runtime.Verify(candidate => candidate.WarmUpAsync(It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task StartAsync_LoadedButNotReadyModel_RetriesWarmup()
	{
		Mock<IEmbeddingModelRuntime> runtime = new();
		runtime.SetupGet(candidate => candidate.IsProvisioned).Returns(true);
		runtime.SetupGet(candidate => candidate.IsLoaded).Returns(true);
		runtime.SetupGet(candidate => candidate.IsReady).Returns(false);
		runtime.Setup(candidate => candidate.WarmUpAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		EmbeddingModelWarmupService service = new(
			runtime.Object,
			NullLogger<EmbeddingModelWarmupService>.Instance);

		await service.StartAsync(CancellationToken.None);
		await WaitForAsync(() => runtime.Invocations.Any(
			invocation => invocation.Method.Name == nameof(IEmbeddingModelRuntime.WarmUpAsync)));
		await service.StopAsync(CancellationToken.None);

		runtime.Verify(candidate => candidate.WarmUpAsync(It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task StartAsync_AlreadyReadyModel_DoesNotWarmAgain()
	{
		Mock<IEmbeddingModelRuntime> runtime = new();
		runtime.SetupGet(candidate => candidate.IsProvisioned).Returns(true);
		runtime.SetupGet(candidate => candidate.IsLoaded).Returns(true);
		runtime.SetupGet(candidate => candidate.IsReady).Returns(true);
		EmbeddingModelWarmupService service = new(
			runtime.Object,
			NullLogger<EmbeddingModelWarmupService>.Instance);

		await service.StartAsync(CancellationToken.None);
		await WaitForAsync(() => runtime.Invocations.Any(invocation => invocation.Method.Name == "get_IsReady"));
		await service.StopAsync(CancellationToken.None);

		runtime.Verify(candidate => candidate.WarmUpAsync(It.IsAny<CancellationToken>()), Times.Never);
	}

	private static async Task WaitForAsync(Func<bool> condition)
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
		while (!condition())
		{
			await Task.Delay(10, timeout.Token);
		}

		condition().Should().BeTrue();
	}
}
