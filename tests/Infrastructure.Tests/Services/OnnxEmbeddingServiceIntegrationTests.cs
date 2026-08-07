using System.Diagnostics;
using FluentAssertions;
using Infrastructure.Services;
using Infrastructure.Tests.Fixtures;

namespace Infrastructure.Tests.Services;

[Trait("Category", "Integration")]
public class OnnxEmbeddingServiceIntegrationTests : IClassFixture<OnnxEmbeddingServiceFixture>
{
	private readonly OnnxEmbeddingService _service;

	public OnnxEmbeddingServiceIntegrationTests(OnnxEmbeddingServiceFixture fixture)
	{
		_service = fixture.Service;
	}

	[Fact]
	public void IsConfigured_ReturnsTrue()
	{
		_service.IsConfigured.Should().BeTrue();
	}

	[Fact]
	public async Task GenerateEmbeddingAsync_ReturnsCorrectDimension()
	{
		float[] embedding = await _service.GenerateEmbeddingAsync("test text", CancellationToken.None);

		embedding.Should().HaveCount(OnnxEmbeddingService.EmbeddingDimension);
	}

	[Fact]
	public async Task GenerateEmbeddingAsync_ReturnsNormalizedVector()
	{
		float[] embedding = await _service.GenerateEmbeddingAsync("hello world", CancellationToken.None);

		float norm = MathF.Sqrt(embedding.Sum(x => x * x));
		norm.Should().BeApproximately(1.0f, 1e-5f);
	}

	[Fact]
	public async Task GenerateEmbeddingAsync_SimilarTexts_HaveHighCosineSimilarity()
	{
		float[] a = await _service.GenerateEmbeddingAsync("fresh organic milk", CancellationToken.None);
		float[] b = await _service.GenerateEmbeddingAsync("organic whole milk", CancellationToken.None);

		double similarity = CosineSimilarity(a, b);

		similarity.Should().BeGreaterThan(0.7);
	}

	[Fact]
	public async Task GenerateEmbeddingAsync_DissimilarTexts_HaveLowCosineSimilarity()
	{
		float[] a = await _service.GenerateEmbeddingAsync("fresh organic milk", CancellationToken.None);
		float[] b = await _service.GenerateEmbeddingAsync("automotive brake pads", CancellationToken.None);

		double similarity = CosineSimilarity(a, b);

		// BGE-large produces elevated baseline similarities vs MiniLM: cross-domain English
		// pairs typically land in 0.4–0.5 rather than the 0.1–0.3 range of smaller models.
		// Threshold set with headroom above the typical upper bound so the test isn't fragile
		// to normal run-to-run variation — see calibration-results.md.
		similarity.Should().BeLessThan(0.6);
	}

	[Fact]
	public async Task GenerateEmbeddingsAsync_BatchProcessing_ReturnsCorrectCount()
	{
		List<string> texts = ["apples", "bananas", "oranges", "grapes", "milk", "bread", "cheese", "butter", "eggs", "flour"];

		List<float[]> embeddings = await _service.GenerateEmbeddingsAsync(texts, CancellationToken.None);

		embeddings.Should().HaveCount(10);
		embeddings.Should().AllSatisfy(e => e.Should().HaveCount(OnnxEmbeddingService.EmbeddingDimension));
	}

	[Fact]
	public async Task GenerateEmbeddingsAsync_RespectsCancellation()
	{
		List<string> texts = Enumerable.Range(0, 100).Select(i => $"item number {i} description text").ToList();
		using CancellationTokenSource cts = new();
		await cts.CancelAsync();

		Func<Task> act = () => _service.GenerateEmbeddingsAsync(texts, cts.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task GenerateEmbeddingAsync_EmptyString_DoesNotThrow()
	{
		Func<Task> act = () => _service.GenerateEmbeddingAsync("", CancellationToken.None);

		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task GenerateEmbeddingAsync_LongText_ProducesValidEmbedding()
	{
		// Generate text longer than 256 tokens
		string longText = string.Join(" ", Enumerable.Range(0, 500).Select(i => $"word{i}"));

		float[] embedding = await _service.GenerateEmbeddingAsync(longText, CancellationToken.None);

		embedding.Should().HaveCount(OnnxEmbeddingService.EmbeddingDimension);
		float norm = MathF.Sqrt(embedding.Sum(x => x * x));
		norm.Should().BeApproximately(1.0f, 1e-5f);
	}

	[Fact]
	public async Task GenerateEmbeddingAsync_Performance_CompletesWithinThreshold()
	{
		// Warm-up
		await _service.GenerateEmbeddingAsync("warm up call", CancellationToken.None);

		Stopwatch sw = Stopwatch.StartNew();
		await _service.GenerateEmbeddingAsync("performance test input", CancellationToken.None);
		sw.Stop();

		sw.ElapsedMilliseconds.Should().BeLessThan(500, "a single embedding should complete within 500ms after warm-up");
	}

	[Fact]
	public void Dispose_CanBeCalledMultipleTimes()
	{
		// Create a separate instance for this test since the fixture's instance is shared
		using OnnxEmbeddingService service = new(
			Microsoft.Extensions.Options.Options.Create(new EmbeddingModelOptions()),
			new Microsoft.Extensions.Logging.Abstractions.NullLogger<OnnxEmbeddingService>());

		Action act = () =>
		{
			service.Dispose();
			service.Dispose();
		};

		act.Should().NotThrow();
	}

	[Fact]
	public async Task GenerateEmbeddingAsync_DeterministicOutput()
	{
		float[] first = await _service.GenerateEmbeddingAsync("deterministic test", CancellationToken.None);
		float[] second = await _service.GenerateEmbeddingAsync("deterministic test", CancellationToken.None);

		first.Should().BeEquivalentTo(second);
	}

	private static double CosineSimilarity(float[] a, float[] b)
	{
		double dot = 0;
		for (int i = 0; i < a.Length; i++)
		{
			dot += a[i] * b[i];
		}

		// Both vectors are L2-normalized, so dot product = cosine similarity
		return dot;
	}
}
