using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.IntegrationTests.CatalogRecall;

/// <summary>One warmed pinned model for the whole lane; the runner provisions and verifies it first.</summary>
public sealed class CatalogRecallModelFixture : IAsyncLifetime
{
	public OnnxEmbeddingService Service { get; } = new(
		Options.Create(new EmbeddingModelOptions { ModelPath = Environment.GetEnvironmentVariable("Embeddings__ModelPath") }),
		NullLogger<OnnxEmbeddingService>.Instance);

	public async Task InitializeAsync()
	{
		await Service.WarmUpAsync(CancellationToken.None);
		if (!Service.IsConfigured)
		{
			throw new InvalidOperationException(
				"The pinned ONNX model is not ready; run `dotnet run scripts/run-prerequisite-tests.cs -- catalog-recall`.");
		}
	}

	public Task DisposeAsync()
	{
		Service.Dispose();
		return Task.CompletedTask;
	}
}

[CollectionDefinition(Name)]
public sealed class CatalogRecallCollection : ICollectionFixture<CatalogRecallModelFixture>
{
	public const string Name = "CatalogRecall";
}
