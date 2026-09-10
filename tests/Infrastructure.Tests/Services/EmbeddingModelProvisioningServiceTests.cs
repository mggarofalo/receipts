using System.Net;
using System.Security.Cryptography;

using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Services;

/// <summary>
/// Covers the startup check that decides whether the 1.34 GB download can be skipped.
/// Uses a synthetic file set so the assertions run against bytes we can actually create.
/// </summary>
public class EmbeddingModelProvisioningServiceTests : IDisposable
{
	private static readonly IReadOnlyList<EmbeddingModelFile> TestFiles =
	[
		new("model.onnx", "onnx/model.onnx", 4L, "0000000000000000000000000000000000000000000000000000000000000000"),
		new("vocab.txt", "vocab.txt", 3L, "1111111111111111111111111111111111111111111111111111111111111111"),
	];

	private readonly string _directory =
		Path.Combine(Path.GetTempPath(), "receipts-onnx-tests", Guid.NewGuid().ToString("N"));

	public EmbeddingModelProvisioningServiceTests() => Directory.CreateDirectory(_directory);

	[Fact]
	public void IsProvisioned_NothingOnDisk_ReturnsFalse()
	{
		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeFalse();
	}

	[Fact]
	public void IsProvisioned_FilesPresentButNoMarker_ReturnsFalse()
	{
		// Arrange — an interrupted run can leave correct-looking files behind; without the
		// marker we have never confirmed their digests, so they must not be trusted.
		WriteFiles();

		// Act / Assert
		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeFalse();
	}

	[Fact]
	public void IsProvisioned_MarkerNamesADifferentRevision_ReturnsFalse()
	{
		// Arrange — this is what makes bumping the pinned revision re-provision.
		WriteFiles();
		WriteMarker("0000000000000000000000000000000000000000");

		// Act / Assert
		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeFalse();
	}

	[Fact]
	public void IsProvisioned_FileTruncated_ReturnsFalse()
	{
		// Arrange — a half-written file with a valid marker is the dangerous case: it would
		// otherwise be handed to InferenceSession and crash on load.
		WriteFiles();
		WriteMarker(EmbeddingModelOptions.VerifiedMarker);
		File.WriteAllText(Path.Combine(_directory, "model.onnx"), "x");

		// Act / Assert
		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeFalse();
	}

	[Fact]
	public void IsProvisioned_OneFileMissing_ReturnsFalse()
	{
		// Arrange
		WriteFiles();
		WriteMarker(EmbeddingModelOptions.VerifiedMarker);
		File.Delete(Path.Combine(_directory, "vocab.txt"));

		// Act / Assert
		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeFalse();
	}

	[Fact]
	public void IsProvisioned_MarkerMatchesAndFilesIntact_ReturnsTrue()
	{
		// Arrange
		WriteFiles();
		WriteMarker(EmbeddingModelOptions.VerifiedMarker);

		// Act / Assert
		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeTrue();
	}

	[Fact]
	public void IsProvisioned_MarkerHasSurroundingWhitespace_StillMatches()
	{
		// Arrange — tolerate a trailing newline from a hand-staged air-gapped install.
		WriteFiles();
		File.WriteAllText(
			Path.Combine(_directory, EmbeddingModelOptions.MarkerFileName),
			$"  {EmbeddingModelOptions.VerifiedMarker}\r\n");

		// Act / Assert
		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeTrue();
	}

	[Fact]
	public void IsProvisioned_DirectoryDoesNotExist_ReturnsFalseRatherThanThrowing()
	{
		string missing = Path.Combine(_directory, "nope");

		EmbeddingModelProvisioningService.IsProvisioned(missing, TestFiles).Should().BeFalse();
	}

	[Fact]
	public void IsProvisioned_LegacyRevisionOnlyMarkerAndIntactLengths_ReturnsFalse()
	{
		WriteFiles();
		WriteMarker(EmbeddingModelOptions.Revision);

		EmbeddingModelProvisioningService.IsProvisioned(_directory, TestFiles).Should().BeFalse();
	}

	[Fact]
	public async Task VerifyExistingSetAndPublishMarkerAsync_ValidCompleteSet_PublishesVerifiedMarker()
	{
		byte[] modelBytes = [1, 2, 3, 4];
		byte[] vocabBytes = [5, 6, 7];
		EmbeddingModelFile model = FileDefinition("model.onnx", modelBytes);
		EmbeddingModelFile vocab = FileDefinition("vocab.txt", vocabBytes);
		File.WriteAllBytes(Path.Combine(_directory, model.FileName), modelBytes);
		File.WriteAllBytes(Path.Combine(_directory, vocab.FileName), vocabBytes);
		WriteMarker(EmbeddingModelOptions.Revision);

		bool verified = await EmbeddingModelProvisioningService.VerifyExistingSetAndPublishMarkerAsync(
			_directory,
			[model, vocab],
			CancellationToken.None);

		verified.Should().BeTrue();
		File.ReadAllText(Path.Combine(_directory, EmbeddingModelOptions.MarkerFileName))
			.Should().Be(EmbeddingModelOptions.VerifiedMarker);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task VerifyExistingSetAndPublishMarkerAsync_InvalidMember_DoesNotPublishVerifiedMarker(
		bool secondFileMissing)
	{
		byte[] firstBytes = [1, 3, 5];
		byte[] secondBytes = [2, 4, 6, 8];
		EmbeddingModelFile first = FileDefinition("first.bin", firstBytes);
		EmbeddingModelFile second = FileDefinition("second.bin", secondBytes);
		File.WriteAllBytes(Path.Combine(_directory, first.FileName), firstBytes);
		if (!secondFileMissing)
		{
			File.WriteAllBytes(Path.Combine(_directory, second.FileName), [8, 6, 4, 2]);
		}
		WriteMarker(EmbeddingModelOptions.Revision);

		bool verified = await EmbeddingModelProvisioningService.VerifyExistingSetAndPublishMarkerAsync(
			_directory,
			[first, second],
			CancellationToken.None);

		verified.Should().BeFalse();
		File.ReadAllText(Path.Combine(_directory, EmbeddingModelOptions.MarkerFileName))
			.Should().NotBe(EmbeddingModelOptions.VerifiedMarker);
	}

	[Fact]
	public void ProvisioningSources_MarkerFormatsAndOfflineFailureContractRemainAligned()
	{
		string repositoryRoot = FindRepositoryRoot();
		string script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "download-onnx-model.cs"));

		EmbeddingModelOptions.VerifiedMarker.Should().Be($"sha256-v1:{EmbeddingModelOptions.Revision}");
		script.Should().Contain($"const string Revision = \"{EmbeddingModelOptions.Revision}\";");
		script.Should().Contain("const string VerifiedMarker = \"sha256-v1:\" + Revision;");
		script.Should().Contain("&& MarkerMatches(markerPath, VerifiedMarker)");
		script.Should().Contain("&& files.All(file =>");
		script.Should().Contain("existing.Exists && existing.Length == file.Size");
		script.Should().Contain("File.WriteAllTextAsync(markerPath, VerifiedMarker)");
	}

	[Fact]
	public async Task VerifyLocalFilesWithoutDownloadAsync_MarkerPublishFails_LogsAndCompletes()
	{
		byte[] contents = [11, 22, 33];
		EmbeddingModelFile file = FileDefinition("model.bin", contents);
		File.WriteAllBytes(Path.Combine(_directory, file.FileName), contents);
		Directory.CreateDirectory(Path.Combine(_directory, EmbeddingModelOptions.MarkerFileName));
		ErrorCountingLogger logger = new();
		EmbeddingModelProvisioningService service = CreateService(new QueueMessageHandler(), logger);

		Func<Task> act = () =>
			service.VerifyLocalFilesWithoutDownloadAsync(_directory, [file], CancellationToken.None);

		await act.Should().NotThrowAsync();
		logger.ErrorCount.Should().Be(1);
	}

	[Fact]
	public async Task VerifyLocalFilesWithoutDownloadAsync_Cancelled_CompletesWithoutError()
	{
		byte[] contents = [44, 55, 66];
		EmbeddingModelFile file = FileDefinition("model.bin", contents);
		File.WriteAllBytes(Path.Combine(_directory, file.FileName), contents);
		ErrorCountingLogger logger = new();
		EmbeddingModelProvisioningService service = CreateService(new QueueMessageHandler(), logger);
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		Func<Task> act = () =>
			service.VerifyLocalFilesWithoutDownloadAsync(_directory, [file], cancellation.Token);

		await act.Should().NotThrowAsync();
		logger.ErrorCount.Should().Be(0);
		File.Exists(Path.Combine(_directory, EmbeddingModelOptions.MarkerFileName)).Should().BeFalse();
	}

	[Theory]
	[InlineData(null)]
	[InlineData("previous-revision")]
	public async Task ProvisionAsync_UntrustedEqualLengthFile_DownloadsAndReplacesIt(string? marker)
	{
		byte[] expected = [1, 2, 3, 4];
		byte[] corrupt = [4, 3, 2, 1];
		EmbeddingModelFile file = FileDefinition("model.onnx", expected);
		File.WriteAllBytes(Path.Combine(_directory, file.FileName), corrupt);
		if (marker is not null)
		{
			WriteMarker(marker);
		}

		QueueMessageHandler handler = new(expected);
		EmbeddingModelProvisioningService service = CreateService(handler);

		await service.ProvisionAsync(_directory, [file], CancellationToken.None);

		handler.RequestCount.Should().Be(1);
		File.ReadAllBytes(Path.Combine(_directory, file.FileName)).Should().Equal(expected);
		File.ReadAllText(Path.Combine(_directory, EmbeddingModelOptions.MarkerFileName))
			.Should().Be(EmbeddingModelOptions.VerifiedMarker);
	}

	[Fact]
	public async Task ProvisionAsync_FileDigestMatches_DoesNotDownload()
	{
		byte[] expected = [10, 20, 30];
		EmbeddingModelFile file = FileDefinition("vocab.txt", expected);
		File.WriteAllBytes(Path.Combine(_directory, file.FileName), expected);
		QueueMessageHandler handler = new();
		EmbeddingModelProvisioningService service = CreateService(handler);

		await service.ProvisionAsync(_directory, [file], CancellationToken.None);

		handler.RequestCount.Should().Be(0);
		File.ReadAllBytes(Path.Combine(_directory, file.FileName)).Should().Equal(expected);
		File.ReadAllText(Path.Combine(_directory, EmbeddingModelOptions.MarkerFileName))
			.Should().Be(EmbeddingModelOptions.VerifiedMarker);
	}

	[Fact]
	public async Task ProvisionAsync_LaterArtifactFails_PublishesMarkerOnlyAfterSuccessfulRetry()
	{
		byte[] firstBytes = [1, 3, 5];
		byte[] secondBytes = [2, 4, 6, 8];
		byte[] corruptSecondBytes = [8, 6, 4, 2];
		EmbeddingModelFile first = FileDefinition("first.bin", firstBytes);
		EmbeddingModelFile second = FileDefinition("second.bin", secondBytes);
		QueueMessageHandler handler = new(firstBytes, corruptSecondBytes, secondBytes);
		EmbeddingModelProvisioningService service = CreateService(handler);
		string markerPath = Path.Combine(_directory, EmbeddingModelOptions.MarkerFileName);

		Func<Task> firstAttempt = () =>
			service.ProvisionAsync(_directory, [first, second], CancellationToken.None);

		await firstAttempt.Should().ThrowAsync<InvalidOperationException>();
		File.Exists(markerPath).Should().BeFalse();
		File.ReadAllBytes(Path.Combine(_directory, first.FileName)).Should().Equal(firstBytes);
		File.Exists(Path.Combine(_directory, second.FileName)).Should().BeFalse();

		await service.ProvisionAsync(_directory, [first, second], CancellationToken.None);

		handler.RequestCount.Should().Be(3);
		File.ReadAllText(markerPath).Should().Be(EmbeddingModelOptions.VerifiedMarker);
		File.ReadAllBytes(Path.Combine(_directory, second.FileName)).Should().Equal(secondBytes);
	}

	private static EmbeddingModelProvisioningService CreateService(
		HttpMessageHandler handler,
		ILogger<EmbeddingModelProvisioningService>? logger = null)
	{
		HttpClient client = new(handler);
		TestHttpClientFactory factory = new(client);
		return new(
			Options.Create(new EmbeddingModelOptions { BaseUrl = "https://models.test" }),
			factory,
			logger ?? NullLogger<EmbeddingModelProvisioningService>.Instance);
	}

	private static EmbeddingModelFile FileDefinition(string fileName, byte[] contents) =>
		new(
			fileName,
			fileName,
			contents.LongLength,
			Convert.ToHexStringLower(SHA256.HashData(contents)));

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Receipts.slnx")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName
			?? throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
	}

	private void WriteFiles()
	{
		File.WriteAllText(Path.Combine(_directory, "model.onnx"), "ABCD");
		File.WriteAllText(Path.Combine(_directory, "vocab.txt"), "abc");
	}

	private void WriteMarker(string revision) =>
		File.WriteAllText(Path.Combine(_directory, EmbeddingModelOptions.MarkerFileName), revision);

	private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
	{
		public HttpClient CreateClient(string name)
		{
			name.Should().Be(EmbeddingModelProvisioningService.HttpClientName);
			return client;
		}
	}

	private sealed class QueueMessageHandler(params byte[][] responses) : HttpMessageHandler
	{
		private readonly Queue<byte[]> _responses = new(responses);

		public int RequestCount { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			RequestCount++;
			if (_responses.Count == 0)
			{
				throw new InvalidOperationException($"Unexpected HTTP request to {request.RequestUri}");
			}

			HttpResponseMessage response = new(HttpStatusCode.OK)
			{
				Content = new ByteArrayContent(_responses.Dequeue()),
				RequestMessage = request,
			};
			return Task.FromResult(response);
		}
	}

	private sealed class ErrorCountingLogger : ILogger<EmbeddingModelProvisioningService>
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

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_directory))
			{
				Directory.Delete(_directory, recursive: true);
			}
		}
		catch (IOException)
		{
			// Best effort — a leftover temp directory must not fail the run.
		}

		GC.SuppressFinalize(this);
	}
}
