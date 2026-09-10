using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Infrastructure.Tests.Services;

public class EmbeddingModelWarmupServiceTests
{
	[Fact]
	public async Task StartAsync_ConfiguredUnloadedModel_WarmsExplicitly()
	{
		Mock<IEmbeddingModelRuntime> runtime = new();
		runtime.SetupGet(candidate => candidate.IsLoaded).Returns(false);
		runtime.Setup(candidate => candidate.WarmUpAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		Mock<IEmbeddingService> embeddingService = new();
		embeddingService.SetupGet(candidate => candidate.IsConfigured).Returns(true);
		EmbeddingModelWarmupService service = new(
			runtime.Object,
			embeddingService.Object,
			NullLogger<EmbeddingModelWarmupService>.Instance);

		await service.StartAsync(CancellationToken.None);
		await WaitForAsync(() => runtime.Invocations.Any(
			invocation => invocation.Method.Name == nameof(IEmbeddingModelRuntime.WarmUpAsync)));
		await service.StopAsync(CancellationToken.None);

		runtime.Verify(candidate => candidate.WarmUpAsync(It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task StartAsync_AlreadyLoadedModel_DoesNotWarmAgain()
	{
		Mock<IEmbeddingModelRuntime> runtime = new();
		runtime.SetupGet(candidate => candidate.IsLoaded).Returns(true);
		Mock<IEmbeddingService> embeddingService = new();
		embeddingService.SetupGet(candidate => candidate.IsConfigured).Returns(true);
		EmbeddingModelWarmupService service = new(
			runtime.Object,
			embeddingService.Object,
			NullLogger<EmbeddingModelWarmupService>.Instance);

		await service.StartAsync(CancellationToken.None);
		await WaitForAsync(() => runtime.Invocations.Any(invocation => invocation.Method.Name == "get_IsLoaded"));
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
