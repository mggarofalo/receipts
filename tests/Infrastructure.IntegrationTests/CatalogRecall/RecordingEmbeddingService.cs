using System.Collections.Concurrent;
using Application.Interfaces.Services;

namespace Infrastructure.IntegrationTests.CatalogRecall;

/// <summary>Embeds each text once and keeps the vector, so both modes and the similarity query share one inference.</summary>
internal sealed class RecordingEmbeddingService(IEmbeddingService inner) : IEmbeddingService
{
	private readonly ConcurrentDictionary<string, Task<float[]>> vectors = new(StringComparer.Ordinal);

	public bool IsConfigured => inner.IsConfigured;

	public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
		=> vectors.GetOrAdd(text, value => inner.GenerateEmbeddingAsync(value, cancellationToken));

	public async Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken)
	{
		List<float[]> result = [];
		foreach (string text in texts) { result.Add(await GenerateEmbeddingAsync(text, cancellationToken)); }
		return result;
	}

	/// <summary>The vector retrieval used for this text, or null when retrieval never asked for one.</summary>
	public float[]? Recorded(string text)
		=> vectors.TryGetValue(text, out Task<float[]>? vector) && vector.IsCompletedSuccessfully ? vector.Result : null;
}
