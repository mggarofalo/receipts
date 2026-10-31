using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace Infrastructure.Tests.Services;

[Trait("Category", "Unit")]
public class NormalizationCandidateRetrievalTests
{
	private readonly Mock<IEmbeddingService> _embedding = new(MockBehavior.Strict);
	private readonly Mock<ILogger> _logger = new();

	[Fact]
	public async Task TryEmbedAsync_FullInferenceQueue_DegradesWithAWarning()
	{
		_embedding.Setup(service => service.GenerateEmbeddingAsync("KS ORG CHKN BRST", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new EmbeddingQueueFullException("request"));

		float[]? actual = await NormalizationCandidateRetrieval.TryEmbedAsync(
			_embedding.Object, "KS ORG CHKN BRST", _logger.Object, CancellationToken.None);

		actual.Should().BeNull();
		_logger.Verify(logger => logger.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
			It.IsAny<EmbeddingQueueFullException>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once());
	}

	[Fact]
	public async Task TryEmbedAsync_UnavailableModel_DegradesWithAWarning()
	{
		_embedding.Setup(service => service.GenerateEmbeddingAsync("KS ORG CHKN BRST", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("model unavailable"));

		float[]? actual = await NormalizationCandidateRetrieval.TryEmbedAsync(
			_embedding.Object, "KS ORG CHKN BRST", _logger.Object, CancellationToken.None);

		actual.Should().BeNull();
		_logger.Verify(logger => logger.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
			It.IsAny<InvalidOperationException>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once());
	}

	[Fact]
	public async Task TryEmbedAsync_CallerCancellation_Propagates()
	{
		using CancellationTokenSource cancelled = new();
		await cancelled.CancelAsync();
		_embedding.Setup(service => service.GenerateEmbeddingAsync("KS ORG CHKN BRST", cancelled.Token))
			.ThrowsAsync(new OperationCanceledException(cancelled.Token));

		Func<Task> act = () => NormalizationCandidateRetrieval.TryEmbedAsync(
			_embedding.Object, "KS ORG CHKN BRST", _logger.Object, cancelled.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
		_logger.Verify(logger => logger.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
			It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never());
	}
}
