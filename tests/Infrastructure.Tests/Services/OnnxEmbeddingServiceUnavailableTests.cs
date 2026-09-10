using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Services;

/// <summary>
/// The model is provisioned onto a volume at runtime rather than shipped in the image
/// (RECEIPTS-929), so there is a real window on a fresh deployment where it is absent.
/// These tests pin that behaviour down and need no model of their own, so unlike the
/// Prerequisite=Model suites, they run in the ordinary CI unit lane.
/// </summary>
public class OnnxEmbeddingServiceUnavailableTests : IDisposable
{
	private readonly string _emptyDirectory =
		Path.Combine(Path.GetTempPath(), "receipts-onnx-absent", Guid.NewGuid().ToString("N"));

	private OnnxEmbeddingService CreateService(ILogger<OnnxEmbeddingService>? logger = null) =>
		new(
			Options.Create(new EmbeddingModelOptions { ModelPath = _emptyDirectory }),
			logger ?? NullLogger<OnnxEmbeddingService>.Instance);

	[Fact]
	public void Constructor_ModelMissing_DoesNotThrow()
	{
		// The constructor used to throw FileNotFoundException, which would have taken the
		// whole host down on first boot before the download had a chance to finish.
		Action act = () => CreateService().Dispose();

		act.Should().NotThrow();
	}

	[Fact]
	public void IsConfigured_ModelMissing_ReturnsFalse()
	{
		using OnnxEmbeddingService service = CreateService();

		service.IsConfigured.Should().BeFalse();
	}

	[Fact]
	public void IsConfigured_CalledRepeatedly_StaysFalseWithoutThrowing()
	{
		// Callers poll this; it must not latch into a faulted state or throw on the way.
		using OnnxEmbeddingService service = CreateService();

		for (int i = 0; i < 5; i++)
		{
			service.IsConfigured.Should().BeFalse();
		}
	}

	[Fact]
	public void IsConfigured_ModelFilesWithoutVerifiedMarker_DoesNotAttemptModelLoad()
	{
		Directory.CreateDirectory(_emptyDirectory);
		File.WriteAllBytes(
			Path.Combine(_emptyDirectory, EmbeddingModelOptions.ModelFileName),
			[1, 2, 3, 4]);
		File.WriteAllBytes(
			Path.Combine(_emptyDirectory, EmbeddingModelOptions.VocabFileName),
			[5, 6, 7, 8]);
		ErrorCountingLogger logger = new();
		using OnnxEmbeddingService service = CreateService(logger);

		service.IsConfigured.Should().BeFalse();
		service.IsConfigured.Should().BeFalse();
		logger.ErrorCount.Should().Be(0, "unverified files must be rejected before ONNX loading is attempted");
	}

	[Fact]
	public void Readiness_VerifiedFiles_AreProvisionedButNotConfiguredUntilLoaded()
	{
		WriteInvalidProvisionedModel();
		ErrorCountingLogger logger = new();
		using OnnxEmbeddingService service = CreateService(logger);

		service.IsProvisioned.Should().BeTrue();
		service.IsReady.Should().BeFalse();
		service.IsConfigured.Should().BeFalse();
		service.IsLoaded.Should().BeFalse("availability checks must not allocate an ONNX session");
		logger.ErrorCount.Should().Be(0);
	}

	[Fact]
	public async Task WarmUp_InvalidProvisionedModel_LeavesServiceUnconfigured()
	{
		WriteInvalidProvisionedModel();
		ErrorCountingLogger logger = new();
		using OnnxEmbeddingService service = CreateService(logger);

		Func<Task> act = () => service.WarmUpAsync(CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>();
		service.IsProvisioned.Should().BeTrue();
		service.IsLoaded.Should().BeFalse();
		service.IsReady.Should().BeFalse();
		service.IsConfigured.Should().BeFalse("a failed load must not advertise semantic readiness");
		logger.ErrorCount.Should().Be(1);
	}

	[Fact]
	public async Task GenerateEmbeddingAsync_ModelMissing_ThrowsWithAnActionableMessage()
	{
		using OnnxEmbeddingService service = CreateService();

		Func<Task> act = () => service.GenerateEmbeddingAsync("anything", CancellationToken.None);

		(await act.Should().ThrowAsync<InvalidOperationException>())
			.Which.Message.Should().Contain(_emptyDirectory);
	}

	[Fact]
	public async Task GenerateEmbeddingsAsync_ModelMissing_Throws()
	{
		using OnnxEmbeddingService service = CreateService();

		Func<Task> act = () => service.GenerateEmbeddingsAsync(["a", "b"], CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public void Dispose_NeverLoaded_IsSafeAndIdempotent()
	{
		OnnxEmbeddingService service = CreateService();

		Action act = () =>
		{
			service.Dispose();
			service.Dispose();
		};

		act.Should().NotThrow();
	}

	[Fact]
	public void IsConfigured_AfterDispose_ReturnsFalse()
	{
		OnnxEmbeddingService service = CreateService();
		service.Dispose();

		service.IsConfigured.Should().BeFalse();
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_emptyDirectory))
			{
				Directory.Delete(_emptyDirectory, recursive: true);
			}
		}
		catch (IOException)
		{
			// Best effort.
		}

		GC.SuppressFinalize(this);
	}

	private void WriteInvalidProvisionedModel()
	{
		Directory.CreateDirectory(_emptyDirectory);
		foreach (EmbeddingModelFile file in EmbeddingModelOptions.Files)
		{
			using FileStream stream = File.Create(Path.Combine(_emptyDirectory, file.FileName));
			stream.SetLength(file.SizeBytes);
		}

		File.WriteAllText(
			Path.Combine(_emptyDirectory, EmbeddingModelOptions.MarkerFileName),
			EmbeddingModelOptions.VerifiedMarker);
	}

	private sealed class ErrorCountingLogger : ILogger<OnnxEmbeddingService>
	{
		public int ErrorCount { get; private set; }

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			if (logLevel == LogLevel.Error)
			{
				ErrorCount++;
			}
		}
	}
}
