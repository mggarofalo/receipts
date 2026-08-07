using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Services;

/// <summary>
/// The model is provisioned onto a volume at runtime rather than shipped in the image
/// (RECEIPTS-929), so there is a real window on a fresh deployment where it is absent.
/// These tests pin that behaviour down and need no model of their own, so unlike the
/// Category=Integration suites they run in CI.
/// </summary>
public class OnnxEmbeddingServiceUnavailableTests : IDisposable
{
	private readonly string _emptyDirectory =
		Path.Combine(Path.GetTempPath(), "receipts-onnx-absent", Guid.NewGuid().ToString("N"));

	private OnnxEmbeddingService CreateService() =>
		new(
			Options.Create(new EmbeddingModelOptions { ModelPath = _emptyDirectory }),
			NullLogger<OnnxEmbeddingService>.Instance);

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
}
