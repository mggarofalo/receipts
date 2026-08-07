using Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.Tests.Fixtures;

public class OnnxEmbeddingServiceFixture : IDisposable
{
	private bool _disposed;

	public OnnxEmbeddingService Service { get; }

	public OnnxEmbeddingServiceFixture()
	{
		ILogger<OnnxEmbeddingService> logger = new Mock<ILogger<OnnxEmbeddingService>>().Object;

		// Default options resolve to the per-machine model cache that
		// scripts/download-onnx-model.cs writes to. The model is no longer copied into the
		// test project's output directory (RECEIPTS-929), so a machine that has never
		// downloaded it will not have it here.
		if (!ModelExists)
		{
			throw new InvalidOperationException(
				$"The ONNX embedding model was not found at {ModelDirectory}. " +
				"These tests are tagged Category=Integration and need the real model; " +
				"run `dotnet run scripts/download-onnx-model.cs` to fetch it (~1.34 GB).");
		}

		Service = new OnnxEmbeddingService(Options.Create(ModelOptions), logger);
	}

	private static EmbeddingModelOptions ModelOptions { get; } = new();

	public static string ModelDirectory => ModelOptions.ResolveModelDirectory();

	public static bool ModelExists =>
		File.Exists(Path.Combine(ModelDirectory, EmbeddingModelOptions.ModelFileName))
		&& File.Exists(Path.Combine(ModelDirectory, EmbeddingModelOptions.VocabFileName));

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
