using System.Diagnostics;
using FluentAssertions;
using Infrastructure.Services;
using Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Infrastructure.Tests.Services;

[Trait("Category", "Integration")]
[Trait("Prerequisite", "Model")]
[Collection("Embedding model performance")]
public class EmbeddingInferenceBenchmarkTests(ITestOutputHelper output)
{
	[Fact]
	public async Task ColdWarmAndContendedRequestLatency_RecordsPercentilesAndProcessMemory()
	{
		string modelDirectory = OnnxEmbeddingServiceFixture.ResolveModelDirectory(
			Environment.GetEnvironmentVariable("Embeddings__ModelPath"));
		using OnnxEmbeddingService service = new(
			Options.Create(new EmbeddingModelOptions
			{
				ModelPath = modelDirectory,
				RequestQueueCapacity = 32,
				BackgroundQueueCapacity = 32,
			}),
			NullLogger<OnnxEmbeddingService>.Instance);
		service.IsProvisioned.Should().BeTrue(
			$"the pinned model and verified marker must exist at {modelDirectory}");
		service.IsConfigured.Should().BeFalse("this service instance has not warmed up yet");

		using Process process = Process.GetCurrentProcess();
		process.Refresh();
		long privateMemoryBefore = process.PrivateMemorySize64;
		long workingSetBefore = process.WorkingSet64;
		Stopwatch cold = Stopwatch.StartNew();
		await service.WarmUpAsync(CancellationToken.None);
		cold.Stop();
		service.IsLoaded.Should().BeTrue();
		service.IsReady.Should().BeTrue();
		service.IsConfigured.Should().BeTrue();
		process.Refresh();
		long privateMemoryAfter = process.PrivateMemorySize64;
		long workingSetAfter = process.WorkingSet64;

		List<double> warmLatencies = [];
		for (int i = 0; i < 10; i++)
		{
			warmLatencies.Add(await MeasureRequestAsync(service, $"warm latency sample {i}"));
		}

		List<string> backgroundTexts = Enumerable.Range(0, 24)
			.Select(index => $"background receipt item description {index}")
			.ToList();
		IBackgroundEmbeddingService backgroundService = service;
		List<Task<float[]>> backgroundTasks = backgroundTexts
			.Select(text => backgroundService.GenerateBackgroundEmbeddingAsync(text, CancellationToken.None))
			.ToList();
		backgroundTasks.Should().Contain(task => !task.IsCompleted,
			"request measurements must begin while an admitted background inference is outstanding");
		List<double> contendedRequestLatencies = [];
		for (int i = 0; i < 20; i++)
		{
			backgroundTasks.Should().Contain(task => !task.IsCompleted,
				$"request sample {i} must overlap a concrete queued or in-flight background inference");
			contendedRequestLatencies.Add(
				await MeasureRequestAsync(service, $"interactive receipt lookup {i}"));
		}

		float[][] backgroundResults = await Task.WhenAll(backgroundTasks);

		backgroundResults.Should().HaveCount(backgroundTexts.Count);
		warmLatencies.Should().HaveCount(10);
		contendedRequestLatencies.Should().HaveCount(20);
		output.WriteLine(
			"embedding_benchmark cold_ms={0:F2} warm_p50_ms={1:F2} warm_p95_ms={2:F2} " +
			"contended_request_p50_ms={3:F2} contended_request_p95_ms={4:F2} " +
			"working_set_before_bytes={5} working_set_after_bytes={6} working_set_delta_bytes={7} " +
			"private_memory_before_bytes={8} private_memory_after_bytes={9} private_memory_delta_bytes={10}",
			cold.Elapsed.TotalMilliseconds,
			Percentile(warmLatencies, 0.50),
			Percentile(warmLatencies, 0.95),
			Percentile(contendedRequestLatencies, 0.50),
			Percentile(contendedRequestLatencies, 0.95),
			workingSetBefore,
			workingSetAfter,
			workingSetAfter - workingSetBefore,
			privateMemoryBefore,
			privateMemoryAfter,
			privateMemoryAfter - privateMemoryBefore);
	}

	private static async Task<double> MeasureRequestAsync(OnnxEmbeddingService service, string text)
	{
		long started = Stopwatch.GetTimestamp();
		float[] result = await service.GenerateEmbeddingAsync(text, CancellationToken.None);
		result.Should().HaveCount(OnnxEmbeddingService.EmbeddingDimension);
		return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
	}

	private static double Percentile(IEnumerable<double> samples, double percentile)
	{
		double[] ordered = [.. samples.Order()];
		int index = Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1);
		return ordered[index];
	}
}

[CollectionDefinition("Embedding model performance", DisableParallelization = true)]
public sealed class EmbeddingModelPerformanceCollection;
