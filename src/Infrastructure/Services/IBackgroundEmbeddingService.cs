namespace Infrastructure.Services;

internal interface IBackgroundEmbeddingService
{
	Task<float[]> GenerateBackgroundEmbeddingAsync(string text, CancellationToken cancellationToken);
}
