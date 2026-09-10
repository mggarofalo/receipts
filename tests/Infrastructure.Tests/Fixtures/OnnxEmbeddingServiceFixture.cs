using Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.Tests.Fixtures;

public class OnnxEmbeddingServiceFixture : IDisposable
{
	private bool _disposed;

	public OnnxEmbeddingService Service { get; }
	public string ModelDirectory { get; }

	public OnnxEmbeddingServiceFixture()
		: this(Environment.GetEnvironmentVariable("Embeddings__ModelPath"))
	{
	}

	internal OnnxEmbeddingServiceFixture(string? configuredModelPath)
	{
		ILogger<OnnxEmbeddingService> logger = new Mock<ILogger<OnnxEmbeddingService>>().Object;
		EmbeddingModelOptions modelOptions = CreateModelOptions(configuredModelPath);
		ModelDirectory = modelOptions.ResolveModelDirectory();

		// Default options resolve to the per-machine model cache that
		// scripts/download-onnx-model.cs writes to. The model is no longer copied into the
		// test project's output directory (RECEIPTS-929), so a machine that has never
		// downloaded it will not have it here.
		if (!ModelExists(ModelDirectory))
		{
			throw new InvalidOperationException(
				$"The ONNX embedding model was not found at {ModelDirectory}. " +
				"These tests are tagged Category=Integration and Prerequisite=Model; " +
				"run `dotnet run scripts/download-onnx-model.cs` to fetch it (~1.34 GB).");
		}

		Service = new OnnxEmbeddingService(Options.Create(modelOptions), logger);
	}

	internal static string ResolveModelDirectory(string? configuredModelPath) =>
		CreateModelOptions(configuredModelPath).ResolveModelDirectory();

	private static EmbeddingModelOptions CreateModelOptions(string? configuredModelPath) =>
		new() { ModelPath = configuredModelPath };

	private static bool ModelExists(string modelDirectory) =>
		File.Exists(Path.Combine(modelDirectory, EmbeddingModelOptions.ModelFileName))
		&& File.Exists(Path.Combine(modelDirectory, EmbeddingModelOptions.VocabFileName));

	public void Dispose()
	{
		if (!_disposed)
		{
			Service.Dispose();
			_disposed = true;
		}

		GC.SuppressFinalize(this);
	}
}
