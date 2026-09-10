namespace Infrastructure.Services;

public interface IEmbeddingModelRuntime
{
	bool IsLoaded { get; }
	Task WarmUpAsync(CancellationToken cancellationToken);
}
