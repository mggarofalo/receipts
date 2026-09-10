using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.Services;
using Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Pgvector;

namespace Infrastructure.Tests.Services;

[Trait("Category", "Unit")]
public class EmbeddingGenerationServiceTests
{
	private readonly Mock<IEmbeddingService> _embeddingServiceMock;
	private readonly Mock<ILogger<EmbeddingGenerationService>> _loggerMock;
	private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
	private readonly Mock<IServiceScope> _scopeMock;
	private readonly Mock<IServiceProvider> _serviceProviderMock;
	private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
	private readonly MockCurrentUserAccessor _userAccessor;
	private readonly Mock<ICommittedChangePublisher> _publisherMock = new();

	public EmbeddingGenerationServiceTests()
	{
		_embeddingServiceMock = new Mock<IEmbeddingService>();
		_loggerMock = new Mock<ILogger<EmbeddingGenerationService>>();
		_scopeFactoryMock = new Mock<IServiceScopeFactory>();
		_scopeMock = new Mock<IServiceScope>();
		_serviceProviderMock = new Mock<IServiceProvider>();

		(_contextFactory, _userAccessor) = DbContextWithUserHelpers.CreateInMemoryContextFactoryWithUser();
		_userAccessor.UserId = "test-user";

		_serviceProviderMock
			.Setup(sp => sp.GetService(typeof(IEmbeddingService)))
			.Returns(_embeddingServiceMock.Object);
		_serviceProviderMock
			.Setup(sp => sp.GetService(typeof(IDbContextFactory<ApplicationDbContext>)))
			.Returns(_contextFactory);
		_scopeMock
			.Setup(s => s.ServiceProvider)
			.Returns(_serviceProviderMock.Object);
		_scopeFactoryMock
			.Setup(f => f.CreateScope())
			.Returns(_scopeMock.Object);
	}

	private EmbeddingGenerationService CreatePublisherAwareService() => new(
		_scopeFactoryMock.Object,
		_loggerMock.Object,
		_publisherMock.Object);

	[Fact]
	public async Task ProcessPendingEmbeddingsAsync_CommittedBatch_PublishesBroadRepairAfterSave()
	{
		Guid templateId = Guid.NewGuid();
		await using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.ItemTemplates.Add(new ItemTemplateEntity { Id = templateId, Name = "Organic Milk" });
			await seed.SaveChangesAsync();
		}
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync([CreateFakeEmbedding()]);
		_publisherMock
			.Setup(p => p.PublishAsync(It.IsAny<CommittedEntityChange>()))
			.Callback(() =>
			{
				using ApplicationDbContext committed = _contextFactory.CreateDbContext();
				committed.ItemEmbeddings.Should().ContainSingle(e =>
					e.EntityType == "ItemTemplate" && e.EntityId == templateId,
					"the embedding batch must be durable before publication");
			})
			.Returns(Task.CompletedTask);

		int processed = await CreatePublisherAwareService()
			.ProcessPendingEmbeddingsAsync(CancellationToken.None);

		processed.Should().Be(1);
		_publisherMock.Verify(p => p.PublishAsync(It.Is<CommittedEntityChange>(change =>
			change.EntityType == CommittedEntityType.ItemEmbedding
			&& change.ChangeType == CommittedChangeType.Updated
			&& change.EntityId == null
			&& change.SuppressToast)), Times.Once);
	}

	[Theory]
	[InlineData("ItemTemplate")]
	[InlineData("ReceiptItem")]
	public async Task ProcessPendingEmbeddingsAsync_SourceChangesDuringGeneration_SkipsStaleInference(
		string entityType)
	{
		Guid entityId = Guid.NewGuid();
		await using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			if (entityType == "ItemTemplate")
			{
				seed.ItemTemplates.Add(new ItemTemplateEntity
				{
					Id = entityId,
					Name = "Original Template",
				});
			}
			else
			{
				Guid receiptId = Guid.NewGuid();
				seed.Receipts.Add(new ReceiptEntity
				{
					Id = receiptId,
					Date = new DateOnly(2026, 9, 9),
					Location = "Test",
					TaxAmount = 0,
					TaxAmountCurrency = Common.Currency.USD,
				});
				seed.ReceiptItems.Add(new ReceiptItemEntity
				{
					Id = entityId,
					ReceiptId = receiptId,
					Description = "Original Item",
					Quantity = 1,
					UnitPrice = 1,
					UnitPriceCurrency = Common.Currency.USD,
					TotalAmount = 1,
					TotalAmountCurrency = Common.Currency.USD,
					Category = "Test",
				});
			}
			await seed.SaveChangesAsync();
		}
		TaskCompletionSource generationStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseGeneration = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
			.Returns(async () =>
			{
				generationStarted.SetResult();
				await releaseGeneration.Task;
				return [CreateFakeEmbedding()];
			});
		Task<int> processing = CreatePublisherAwareService()
			.ProcessPendingEmbeddingsAsync(CancellationToken.None);
		await generationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await using (ApplicationDbContext mutate = _contextFactory.CreateDbContext())
		{
			if (entityType == "ItemTemplate")
			{
				ItemTemplateEntity source = await mutate.ItemTemplates
					.IgnoreQueryFilters()
					.SingleAsync(item => item.Id == entityId);
				source.Name = "Renamed While Generating";
			}
			else
			{
				ReceiptItemEntity source = await mutate.ReceiptItems
					.IgnoreQueryFilters()
					.SingleAsync(item => item.Id == entityId);
				source.DeletedAt = DateTimeOffset.UtcNow;
			}
			await mutate.SaveChangesAsync();
		}
		releaseGeneration.SetResult();

		int processed = await processing;

		processed.Should().Be(0);
		await using ApplicationDbContext verify = _contextFactory.CreateDbContext();
		verify.ItemEmbeddings.Should().NotContain(e =>
			e.EntityType == entityType && e.EntityId == entityId);
		_publisherMock.Verify(p => p.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Never);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ProcessPendingEmbeddingsAsync_NoWritableBatch_DoesNotPublish(bool configured)
	{
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(configured);

		int processed = await CreatePublisherAwareService()
			.ProcessPendingEmbeddingsAsync(CancellationToken.None);

		processed.Should().Be(0);
		_publisherMock.Verify(p => p.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Never);
	}

	[Fact]
	public async Task ProcessPendingEmbeddingsAsync_GenerationFailure_DoesNotPublish()
	{
		await using (ApplicationDbContext seed = _contextFactory.CreateDbContext())
		{
			seed.ItemTemplates.Add(new ItemTemplateEntity { Id = Guid.NewGuid(), Name = "Broken Embedding" });
			await seed.SaveChangesAsync();
		}
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("generation failed"));

		Func<Task> act = async () => await CreatePublisherAwareService()
			.ProcessPendingEmbeddingsAsync(CancellationToken.None);

		await act.Should().ThrowAsync<InvalidOperationException>();
		_publisherMock.Verify(p => p.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Never);
	}

	[Fact]
	public async Task ProcessPendingEmbeddingsAsync_SaveFailure_DoesNotPublish()
	{
		MockCurrentUserAccessor accessor = new() { UserId = "test-user" };
		DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase($"EmbeddingSaveFailure_{Guid.NewGuid()}")
			.AddInterceptors(new EmbeddingSaveFailureInterceptor())
			.Options;
		IDbContextFactory<ApplicationDbContext> failingFactory =
			new TestDbContextFactoryWithUser(options, accessor);
		_serviceProviderMock
			.Setup(sp => sp.GetService(typeof(IDbContextFactory<ApplicationDbContext>)))
			.Returns(failingFactory);
		await using (ApplicationDbContext seed = failingFactory.CreateDbContext())
		{
			seed.ItemTemplates.Add(new ItemTemplateEntity { Id = Guid.NewGuid(), Name = "Cannot Save" });
			await seed.SaveChangesAsync();
		}
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync([CreateFakeEmbedding()]);

		Func<Task> act = async () => await CreatePublisherAwareService()
			.ProcessPendingEmbeddingsAsync(CancellationToken.None);

		await act.Should().ThrowAsync<DbUpdateException>();
		_publisherMock.Verify(p => p.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_SkipsProcessing_WhenEmbeddingServiceNotConfigured()
	{
		// Arrange
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(false);

		TaskCompletionSource invoked = new();
		_embeddingServiceMock
			.Setup(e => e.IsConfigured)
			.Returns(() =>
			{
				invoked.TrySetResult();
				return false;
			});

		EmbeddingGenerationService service = new(_scopeFactoryMock.Object, _loggerMock.Object);
		using CancellationTokenSource cts = new();

		// Act
		await service.StartAsync(cts.Token);
		await invoked.Task.WaitAsync(TimeSpan.FromSeconds(15));
		cts.Cancel();
		await service.StopAsync(CancellationToken.None);

		// Assert
		_embeddingServiceMock.Verify(
			e => e.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_ProcessesPendingItemTemplates_CreatesEmbeddings()
	{
		// Arrange
		Guid templateId = Guid.NewGuid();
		using (ApplicationDbContext seedContext = _contextFactory.CreateDbContext())
		{
			seedContext.ItemTemplates.Add(new ItemTemplateEntity
			{
				Id = templateId,
				Name = "Organic Milk",
			});
			await seedContext.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);

		float[] fakeEmbedding = CreateFakeEmbedding();
		TaskCompletionSource invoked = new();
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
			.Returns<List<string>, CancellationToken>((texts, _) =>
			{
				invoked.TrySetResult();
				return Task.FromResult(texts.Select(_ => fakeEmbedding).ToList());
			});

		EmbeddingGenerationService service = new(_scopeFactoryMock.Object, _loggerMock.Object);
		using CancellationTokenSource cts = new();

		// Act
		await service.StartAsync(cts.Token);
		await invoked.Task.WaitAsync(TimeSpan.FromSeconds(15));

		// Wait a short moment for SaveChangesAsync to complete
		await Task.Delay(500);
		cts.Cancel();
		await service.StopAsync(CancellationToken.None);

		// Assert
		using ApplicationDbContext verifyContext = _contextFactory.CreateDbContext();
		List<ItemEmbeddingEntity> embeddings = await verifyContext.ItemEmbeddings.ToListAsync();
		embeddings.Should().ContainSingle();
		embeddings[0].EntityType.Should().Be("ItemTemplate");
		embeddings[0].EntityId.Should().Be(templateId);
		embeddings[0].EntityText.Should().Be("Organic Milk");
		embeddings[0].ModelVersion.Should().Be(OnnxEmbeddingService.ModelName);
	}

	[Fact]
	public async Task ExecuteAsync_UpdatesExistingEmbedding_WhenEntityTextChanged()
	{
		// Arrange
		Guid templateId = Guid.NewGuid();
		Guid embeddingId = Guid.NewGuid();
		using (ApplicationDbContext seedContext = _contextFactory.CreateDbContext())
		{
			seedContext.ItemTemplates.Add(new ItemTemplateEntity
			{
				Id = templateId,
				Name = "Updated Milk Name",
			});
			seedContext.ItemEmbeddings.Add(new ItemEmbeddingEntity
			{
				Id = embeddingId,
				EntityType = "ItemTemplate",
				EntityId = templateId,
				EntityText = "Old Milk Name",
				Embedding = new Vector(CreateFakeEmbedding()),
				ModelVersion = OnnxEmbeddingService.ModelName,
				CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
			});
			await seedContext.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);

		float[] updatedEmbedding = CreateFakeEmbedding();
		TaskCompletionSource invoked = new();
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
			.Returns<List<string>, CancellationToken>((texts, _) =>
			{
				invoked.TrySetResult();
				return Task.FromResult(texts.Select(_ => updatedEmbedding).ToList());
			});

		EmbeddingGenerationService service = new(_scopeFactoryMock.Object, _loggerMock.Object);
		using CancellationTokenSource cts = new();

		// Act
		await service.StartAsync(cts.Token);
		await invoked.Task.WaitAsync(TimeSpan.FromSeconds(15));
		await Task.Delay(500);
		cts.Cancel();
		await service.StopAsync(CancellationToken.None);

		// Assert — should update the existing record, not create a new one
		using ApplicationDbContext verifyContext = _contextFactory.CreateDbContext();
		List<ItemEmbeddingEntity> embeddings = await verifyContext.ItemEmbeddings.ToListAsync();
		embeddings.Should().ContainSingle();
		embeddings[0].Id.Should().Be(embeddingId);
		embeddings[0].EntityText.Should().Be("Updated Milk Name");
	}

	[Fact]
	public async Task ExecuteAsync_SkipsShortNames_WhenLessThanTwoCharacters()
	{
		// Arrange
		using (ApplicationDbContext seedContext = _contextFactory.CreateDbContext())
		{
			seedContext.ItemTemplates.Add(new ItemTemplateEntity
			{
				Id = Guid.NewGuid(),
				Name = "X", // Too short
			});
			await seedContext.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);

		TaskCompletionSource checked_ = new();
		_embeddingServiceMock
			.Setup(e => e.IsConfigured)
			.Returns(() =>
			{
				checked_.TrySetResult();
				return true;
			});

		EmbeddingGenerationService service = new(_scopeFactoryMock.Object, _loggerMock.Object);
		using CancellationTokenSource cts = new();

		// Act
		await service.StartAsync(cts.Token);
		await checked_.Task.WaitAsync(TimeSpan.FromSeconds(15));
		await Task.Delay(500);
		cts.Cancel();
		await service.StopAsync(CancellationToken.None);

		// Assert
		_embeddingServiceMock.Verify(
			e => e.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_LogsError_AndContinues_OnException()
	{
		// Arrange
		using (ApplicationDbContext seedContext = _contextFactory.CreateDbContext())
		{
			seedContext.ItemTemplates.Add(new ItemTemplateEntity
			{
				Id = Guid.NewGuid(),
				Name = "Some Item",
			});
			await seedContext.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);

		TaskCompletionSource invoked = new();
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
			.Returns<List<string>, CancellationToken>((_, _) =>
			{
				invoked.TrySetResult();
				return Task.FromException<List<float[]>>(new InvalidOperationException("ONNX error"));
			});

		EmbeddingGenerationService service = new(_scopeFactoryMock.Object, _loggerMock.Object);
		using CancellationTokenSource cts = new();

		// Act — should not throw
		await service.StartAsync(cts.Token);
		await invoked.Task.WaitAsync(TimeSpan.FromSeconds(15));
		cts.Cancel();
		Func<Task> act = async () => await service.StopAsync(CancellationToken.None);

		// Assert
		await act.Should().NotThrowAsync();
		_loggerMock.Verify(
			x => x.Log(
				LogLevel.Error,
				It.IsAny<EventId>(),
				It.IsAny<It.IsAnyType>(),
				It.IsAny<Exception>(),
				It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
			Times.AtLeastOnce());
	}

	[Fact]
	public async Task ExecuteAsync_StopsGracefully_OnCancellation()
	{
		// Arrange
		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);

		EmbeddingGenerationService service = new(_scopeFactoryMock.Object, _loggerMock.Object);
		using CancellationTokenSource cts = new();

		// Act
		await service.StartAsync(cts.Token);
		cts.Cancel();
		Func<Task> act = async () => await service.StopAsync(CancellationToken.None);

		// Assert
		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task ExecuteAsync_SkipsDeletedItems()
	{
		// Arrange
		using (ApplicationDbContext seedContext = _contextFactory.CreateDbContext())
		{
			seedContext.ItemTemplates.Add(new ItemTemplateEntity
			{
				Id = Guid.NewGuid(),
				Name = "Deleted Item",
				DeletedAt = DateTimeOffset.UtcNow,
			});
			await seedContext.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);

		TaskCompletionSource checked_ = new();
		_embeddingServiceMock
			.Setup(e => e.IsConfigured)
			.Returns(() =>
			{
				checked_.TrySetResult();
				return true;
			});

		EmbeddingGenerationService service = new(_scopeFactoryMock.Object, _loggerMock.Object);
		using CancellationTokenSource cts = new();

		// Act
		await service.StartAsync(cts.Token);
		await checked_.Task.WaitAsync(TimeSpan.FromSeconds(15));
		await Task.Delay(500);
		cts.Cancel();
		await service.StopAsync(CancellationToken.None);

		// Assert
		_embeddingServiceMock.Verify(
			e => e.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_LogsCount_WhenEmbeddingsGenerated()
	{
		// Arrange
		using (ApplicationDbContext seedContext = _contextFactory.CreateDbContext())
		{
			seedContext.ItemTemplates.Add(new ItemTemplateEntity
			{
				Id = Guid.NewGuid(),
				Name = "Test Item",
			});
			await seedContext.SaveChangesAsync();
		}

		_embeddingServiceMock.Setup(e => e.IsConfigured).Returns(true);

		float[] fakeEmbedding = CreateFakeEmbedding();
		TaskCompletionSource invoked = new();
		_embeddingServiceMock
			.Setup(e => e.GenerateEmbeddingsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
			.Returns<List<string>, CancellationToken>((texts, _) =>
			{
				invoked.TrySetResult();
				return Task.FromResult(texts.Select(_ => fakeEmbedding).ToList());
			});

		EmbeddingGenerationService service = new(_scopeFactoryMock.Object, _loggerMock.Object);
		using CancellationTokenSource cts = new();

		// Act
		await service.StartAsync(cts.Token);
		await invoked.Task.WaitAsync(TimeSpan.FromSeconds(15));
		await Task.Delay(500);
		cts.Cancel();
		await service.StopAsync(CancellationToken.None);

		// Assert
		_loggerMock.Verify(
			x => x.Log(
				LogLevel.Information,
				It.IsAny<EventId>(),
				It.IsAny<It.IsAnyType>(),
				It.IsAny<Exception>(),
				It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
			Times.AtLeastOnce());
	}

	[Fact]
	public async Task ExecuteAsync_CancellationDuringInitialDelay_DoesNotThrow()
	{
		// Arrange — cancel immediately so the 10-second initial delay is interrupted
		EmbeddingGenerationService service = new(_scopeFactoryMock.Object, _loggerMock.Object);
		using CancellationTokenSource cts = new();

		// Act — start and cancel right away; the service should be sitting in the initial delay
		await service.StartAsync(cts.Token);
		cts.Cancel();
		Func<Task> act = async () => await service.StopAsync(CancellationToken.None);

		// Assert — the OperationCanceledException from Task.Delay should be caught internally
		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task ExecuteAsync_CancellationDuringLoopDelay_DoesNotThrow()
	{
		// Arrange — let ProcessPendingEmbeddingsAsync run once (short-circuit via IsConfigured=false),
		// then cancel during the 30-second loop delay.
		TaskCompletionSource processed = new();
		_embeddingServiceMock
			.Setup(e => e.IsConfigured)
			.Returns(() =>
			{
				processed.TrySetResult();
				return false;
			});

		EmbeddingGenerationService service = new(_scopeFactoryMock.Object, _loggerMock.Object);
		using CancellationTokenSource cts = new();

		// Act — wait for the first processing cycle to complete, then cancel
		await service.StartAsync(cts.Token);
		await processed.Task.WaitAsync(TimeSpan.FromSeconds(15));
		cts.Cancel();
		Func<Task> act = async () => await service.StopAsync(CancellationToken.None);

		// Assert — the OperationCanceledException from the loop Task.Delay should be caught internally
		await act.Should().NotThrowAsync();
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

	private sealed class EmbeddingSaveFailureInterceptor : SaveChangesInterceptor
	{
		public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
			DbContextEventData eventData,
			InterceptionResult<int> result,
			CancellationToken cancellationToken = default)
		{
			bool writesEmbedding = eventData.Context?.ChangeTracker
				.Entries<ItemEmbeddingEntity>()
				.Any(entry => entry.State is EntityState.Added or EntityState.Modified) == true;
			if (writesEmbedding)
			{
				throw new DbUpdateException("embedding save failed");
			}

			return base.SavingChangesAsync(eventData, result, cancellationToken);
		}
	}
}
