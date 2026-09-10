namespace Infrastructure.Services;

public interface IEmbeddingModelRuntime
{
	bool IsProvisioned { get; }
	bool IsLoaded { get; }
	bool IsReady { get; }
	Task WarmUpAsync(CancellationToken cancellationToken);
}
