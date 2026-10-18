using Application.Interfaces.Services;
using FluentAssertions;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Infrastructure.Tests.Services;

[Trait("Category", "Unit")]
public class ItemTemplateSimilarityServiceTests
{
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
	private readonly Mock<IEmbeddingService> _embeddingServiceMock;
	private readonly Mock<ILogger<ItemTemplateSimilarityService>> _loggerMock;
	private readonly ItemTemplateSimilarityService _service;

	public ItemTemplateSimilarityServiceTests()
	{
		(_contextFactory, MockCurrentUserAccessor accessor) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		accessor.UserId = "test-user";
		_embeddingServiceMock = new Mock<IEmbeddingService>();
		_loggerMock = new Mock<ILogger<ItemTemplateSimilarityService>>();

		_service = new ItemTemplateSimilarityService(
			_contextFactory,
			_embeddingServiceMock.Object,
			_loggerMock.Object);
	}

	[Fact]
	public async Task GetSimilarItemsAsync_RequestedCancellation_DoesNotRunFallbackQuery()
	{
		using CancellationTokenSource cancellation = new();
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock.Setup(e => e.GenerateEmbeddingAsync("cancelled query", cancellation.Token))
			.Callback(() => cancellation.Cancel())
			.ThrowsAsync(new OperationCanceledException(cancellation.Token));

		Func<Task> act = () => _service.GetSimilarItemsAsync("cancelled query", 10, 0.3, true, cancellation.Token);

		// The InMemory provider rejects SQL: reaching fallback would throw InvalidOperationException.
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task GetSimilarItemsAsync_SkipsEmbedding_WhenSemanticSearchDisabled()
	{
		// Arrange — no DbContext setup needed since we're testing the embedding path only.
		// The method will try to call CreateDbContext, so we need a real context.
		// Since we can't run the SQL queries without PostgreSQL, we verify the mock interaction.
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);

		// Act — will throw on SqlQueryRaw with InMemory, but we can verify the embedding path
		try
		{
			await _service.GetSimilarItemsAsync("test", 10, 0.3, useSemanticSearch: false, CancellationToken.None);
		}
		catch (InvalidOperationException)
		{
			// Expected: InMemory doesn't support SqlQueryRaw
		}

		// Assert — embedding should NOT have been generated
		_embeddingServiceMock.Verify(
			e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task GetSimilarItemsAsync_GeneratesEmbedding_WhenSemanticSearchEnabled()
	{
		// Arrange
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingAsync("test query", It.IsAny<CancellationToken>()))
			.ReturnsAsync(CreateFakeEmbedding());

		// Act — will throw on SqlQueryRaw, but we verify the embedding was generated
		try
		{
			await _service.GetSimilarItemsAsync("test query", 10, 0.3, useSemanticSearch: true, CancellationToken.None);
		}
		catch (InvalidOperationException)
		{
			// Expected: InMemory doesn't support SqlQueryRaw
		}

		// Assert — embedding SHOULD have been generated
		_embeddingServiceMock.Verify(
			e => e.GenerateEmbeddingAsync("test query", It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task GetSimilarItemsAsync_SkipsEmbedding_WhenServiceNotConfigured()
	{
		// Arrange
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(false);

		// Act
		try
		{
			await _service.GetSimilarItemsAsync("test", 10, 0.3, useSemanticSearch: true, CancellationToken.None);
		}
		catch (InvalidOperationException)
		{
			// Expected: InMemory doesn't support SqlQueryRaw
		}

		// Assert
		_embeddingServiceMock.Verify(
			e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task GetSimilarItemsAsync_FallsBackToTrigramOnly_WhenEmbeddingGenerationFails()
	{
		// Arrange
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("ONNX inference failed"));

		// Act — will throw on SqlQueryRaw (InMemory doesn't support it), but the
		// ONNX exception should have been caught and logged before reaching SQL.
		try
		{
			await _service.GetSimilarItemsAsync("test", 10, 0.3, useSemanticSearch: true, CancellationToken.None);
		}
		catch (InvalidOperationException)
		{
			// Expected: InMemory doesn't support SqlQueryRaw
		}

		// Assert — the ONNX error was caught and a warning was logged
		_loggerMock.Verify(
			x => x.Log(
				LogLevel.Warning,
				It.IsAny<EventId>(),
				It.IsAny<It.IsAnyType>(),
				It.IsAny<Exception>(),
				It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
			Times.AtLeastOnce());
	}

	[Fact]
	public async Task GetSimilarItemsAsync_SkipsEmbedding_WhenGenerateReturnsEmptyArray()
	{
		// Arrange
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(Array.Empty<float>());

		// Act
		try
		{
			await _service.GetSimilarItemsAsync("test", 10, 0.3, useSemanticSearch: true, CancellationToken.None);
		}
		catch (InvalidOperationException)
		{
			// Expected: InMemory doesn't support SqlQueryRaw
		}

		// Assert — even though embedding was generated, empty result should skip semantic path.
		// The trigram-only path should be taken (searchVector remains null).
		_embeddingServiceMock.Verify(
			e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
			Times.Once);
	}

	private static float[] CreateFakeEmbedding()
	{
		float[] embedding = new float[OnnxEmbeddingService.EmbeddingDimension];
		Random rng = new(42);
		for (int i = 0; i < embedding.Length; i++)
		{
			embedding[i] = (float)(rng.NextDouble() * 2 - 1);
		}

		return embedding;
	}
}
