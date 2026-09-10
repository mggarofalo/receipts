using System.Reflection;
using FluentAssertions;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

public class EmbeddingInferenceQueueTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

	[Fact]
	public async Task BackgroundLane_WhenCapacityIsFull_BackpressuresAdditionalWriter()
	{
		using ManualResetEventSlim inferenceStarted = new();
		using ManualResetEventSlim releaseInference = new();
		using EmbeddingInferenceQueue queue = new(
			text =>
			{
				if (text == "active")
				{
					inferenceStarted.Set();
					releaseInference.Wait(Timeout).Should().BeTrue();
				}

				return [1];
			},
			requestCapacity: 1,
			backgroundCapacity: 1);

		Task<float[]> active = queue.EnqueueBackgroundAsync("active", CancellationToken.None);
		inferenceStarted.Wait(Timeout).Should().BeTrue();
		Task<float[]> buffered = queue.EnqueueBackgroundAsync("buffered", CancellationToken.None);
		using CancellationTokenSource blockedCancellation = new();
		Task<float[]> blocked = queue.EnqueueBackgroundAsync("blocked", blockedCancellation.Token);

		GetBufferedCount(queue, "_background").Should().Be(1,
			"the configured capacity is one even while another producer waits");
		blocked.IsCompleted.Should().BeFalse();

		await blockedCancellation.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
		releaseInference.Set();
		await Task.WhenAll(active, buffered).WaitAsync(Timeout);
	}

	[Fact]
	public async Task Scheduling_InterleavesBackgroundWork_AfterAtMostFourRequests()
	{
		using ManualResetEventSlim firstBackgroundStarted = new();
		using ManualResetEventSlim releaseFirstBackground = new();
		List<string> inferenceOrder = [];
		using EmbeddingInferenceQueue queue = new(
			text =>
			{
				lock (inferenceOrder)
				{
					inferenceOrder.Add(text);
				}

				if (text == "background-1")
				{
					firstBackgroundStarted.Set();
					releaseFirstBackground.Wait(Timeout).Should().BeTrue();
				}

				return [1];
			},
			requestCapacity: 8,
			backgroundCapacity: 8);

		List<Task<float[]>> tasks = [queue.EnqueueBackgroundAsync("background-1", CancellationToken.None)];
		firstBackgroundStarted.Wait(Timeout).Should().BeTrue();
		tasks.Add(queue.EnqueueBackgroundAsync("background-2", CancellationToken.None));
		tasks.Add(queue.EnqueueBackgroundAsync("background-3", CancellationToken.None));
		for (int i = 1; i <= 5; i++)
		{
			tasks.Add(queue.EnqueueRequestAsync($"request-{i}", CancellationToken.None));
		}

		releaseFirstBackground.Set();
		await Task.WhenAll(tasks).WaitAsync(Timeout);

		int secondBackgroundIndex = inferenceOrder.IndexOf("background-2");
		int requestsBeforeSecondBackground = inferenceOrder
			.Take(secondBackgroundIndex)
			.Count(text => text.StartsWith("request-", StringComparison.Ordinal));
		requestsBeforeSecondBackground.Should().BeInRange(1, 4);
		inferenceOrder.IndexOf("request-5").Should().BeGreaterThan(secondBackgroundIndex,
			"a background item must receive capacity before a fifth consecutive request");
	}

	[Fact]
	public async Task QueuedCancellation_SkipsInferenceAndLetsNextItemUseCapacity()
	{
		using ManualResetEventSlim inferenceStarted = new();
		using ManualResetEventSlim releaseInference = new();
		List<string> inferenceOrder = [];
		using EmbeddingInferenceQueue queue = new(
			text =>
			{
				lock (inferenceOrder)
				{
					inferenceOrder.Add(text);
				}

				if (text == "active")
				{
					inferenceStarted.Set();
					releaseInference.Wait(Timeout).Should().BeTrue();
				}

				return [1];
			},
			requestCapacity: 4,
			backgroundCapacity: 1);

		Task<float[]> active = queue.EnqueueRequestAsync("active", CancellationToken.None);
		inferenceStarted.Wait(Timeout).Should().BeTrue();
		using CancellationTokenSource cancellation = new();
		Task<float[]> cancelled = queue.EnqueueRequestAsync("cancelled", cancellation.Token);
		Task<float[]> next = queue.EnqueueRequestAsync("next", CancellationToken.None);
		await cancellation.CancelAsync();

		releaseInference.Set();
		await Task.WhenAll(active, next).WaitAsync(Timeout);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
		inferenceOrder.Should().Equal("active", "next");
	}

	[Fact]
	public async Task Dispose_CancelsEveryPendingCaller_AndCompletesShutdown()
	{
		using ManualResetEventSlim inferenceStarted = new();
		using ManualResetEventSlim releaseInference = new();
		EmbeddingInferenceQueue queue = new(
			text =>
			{
				if (text == "active")
				{
					inferenceStarted.Set();
					releaseInference.Wait(Timeout).Should().BeTrue();
				}

				return [1];
			},
			requestCapacity: 2,
			backgroundCapacity: 2);

		Task<float[]> active = queue.EnqueueRequestAsync("active", CancellationToken.None);
		inferenceStarted.Wait(Timeout).Should().BeTrue();
		Task<float[]> pendingRequest = queue.EnqueueRequestAsync("pending-request", CancellationToken.None);
		Task<float[]> pendingBackground = queue.EnqueueBackgroundAsync("pending-background", CancellationToken.None);
		Task shutdown = Task.Run(queue.Dispose);
		SpinWait.SpinUntil(() => IsShutdownRequested(queue), Timeout).Should().BeTrue();

		releaseInference.Set();
		await shutdown.WaitAsync(Timeout);
		await active.WaitAsync(Timeout);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingRequest);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingBackground);
	}

	private static int GetBufferedCount(EmbeddingInferenceQueue queue, string fieldName)
	{
		object channel = typeof(EmbeddingInferenceQueue)
			.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(queue)!;
		object reader = channel.GetType().GetProperty("Reader")!.GetValue(channel)!;
		return (int)reader.GetType().GetProperty("Count")!.GetValue(reader)!;
	}

	private static bool IsShutdownRequested(EmbeddingInferenceQueue queue) =>
		((CancellationTokenSource)typeof(EmbeddingInferenceQueue)
			.GetField("_shutdown", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(queue)!).IsCancellationRequested;
}
