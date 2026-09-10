using System.Data.Common;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Infrastructure.IntegrationTests.Fixtures;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pgvector;
using SampleData.Entities;

namespace Infrastructure.IntegrationTests.Services;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Prerequisite", "Postgres")]
public class EmbeddingGenerationTrashConcurrencyTests(PostgresFixture fixture)
{
	[Fact]
	public async Task MixedPendingBatch_DeletedDuringInference_AndEmptyTrashCompleteWithoutDeadlockOrStaleEmbeddings()
	{
		Guid templateId = Guid.Parse("00000000-0000-0000-0000-000000000101");
		Guid itemId = Guid.Parse("00000000-0000-0000-0000-000000000102");
		ReceiptEntity receipt = ReceiptEntityGenerator.Generate();
		ItemTemplateEntity template = ItemTemplateEntityGenerator.Generate();
		template.Id = templateId;
		template.Name = "Concurrent template source";
		ReceiptItemEntity item = ReceiptItemEntityGenerator.Generate(receipt.Id);
		item.Id = itemId;
		item.Description = "Concurrent receipt item source";

		await using (ApplicationDbContext seed = fixture.CreateDbContext())
		{
			seed.Receipts.Add(receipt);
			seed.ItemTemplates.Add(template);
			seed.ReceiptItems.Add(item);
			await seed.SaveChangesAsync();

			List<ItemTemplateEntity> otherTemplates = await seed.ItemTemplates.AsNoTracking()
				.Where(row => row.Id != templateId
					&& !seed.ItemEmbeddings.Any(embedding =>
						embedding.EntityType == "ItemTemplate" && embedding.EntityId == row.Id))
				.ToListAsync();
			List<ReceiptItemEntity> otherItems = await seed.ReceiptItems.AsNoTracking()
				.Where(row => row.Id != itemId
					&& !seed.ItemEmbeddings.Any(embedding =>
						embedding.EntityType == "ReceiptItem" && embedding.EntityId == row.Id))
				.ToListAsync();
			List<NormalizedDescriptionEntity> existingCanonicals = await seed.NormalizedDescriptions
				.Where(row => row.Status != Domain.NormalizedDescriptions.NormalizedDescriptionStatus.Rejected)
				.ToListAsync();
			foreach (NormalizedDescriptionEntity canonical in existingCanonicals)
			{
				canonical.Embedding = new Vector(new float[OnnxEmbeddingService.EmbeddingDimension]);
				canonical.EmbeddingModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint;
			}
			seed.ItemEmbeddings.AddRange(otherTemplates.Select(row => BaselineEmbedding("ItemTemplate", row.Id, row.Name)));
			seed.ItemEmbeddings.AddRange(otherItems.Select(row => BaselineEmbedding("ReceiptItem", row.Id, row.Description)));
			await seed.SaveChangesAsync();
		}

		HeldEmbeddingService embeddings = new();
		WorkerLockBarrier workerBarrier = new();
		Mock<ICommittedChangePublisher> publisher = new(MockBehavior.Strict);
		using ServiceProvider provider = BuildWorkerProvider(embeddings, workerBarrier);
		using EmbeddingGenerationService worker = new(
			provider.GetRequiredService<IServiceScopeFactory>(),
			NullLogger<EmbeddingGenerationService>.Instance,
			publisher.Object);

		Task<int> generation = worker.ProcessPendingEmbeddingsAsync(CancellationToken.None);
		IReadOnlyList<string> selected = await embeddings.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
		selected.Should().BeEquivalentTo([template.Name, item.Description]);

		await using (ApplicationDbContext softDelete = fixture.CreateDbContext())
		{
			(await softDelete.ItemTemplates.SingleAsync(row => row.Id == templateId)).DeletedAt = DateTimeOffset.UtcNow;
			(await softDelete.ReceiptItems.SingleAsync(row => row.Id == itemId)).DeletedAt = DateTimeOffset.UtcNow;
			await softDelete.SaveChangesAsync();
		}
		await using (ApplicationDbContext tombstones = fixture.CreateDbContext())
		{
			(await tombstones.ItemTemplates.IgnoreQueryFilters().SingleAsync(row => row.Id == templateId))
				.DeletedAt.Should().NotBeNull();
			(await tombstones.ReceiptItems.IgnoreQueryFilters().SingleAsync(row => row.Id == itemId))
				.DeletedAt.Should().NotBeNull();
		}

		ReceiptDeleteGate purgeGate = new();
		await using ApplicationDbContext purgeContext = new(PurgeOptions(purgeGate));
		Task purge = new TrashService(purgeContext).PurgeAllDeletedAsync(CancellationToken.None);
		await purgeGate.LockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));

		generation.IsCompleted.Should().BeFalse("inference is still held");
		purge.IsCompleted.Should().BeFalse("Empty Trash holds its receipt-item delete lock");

		embeddings.Release.TrySetResult();
		await workerBarrier.TemplateLockCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
		await workerBarrier.ReceiptLockAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
		purge.IsCompleted.Should().BeFalse("the purge barrier has not been released");
		purgeGate.Release.TrySetResult();

		await Task.WhenAll(generation, purge).WaitAsync(TimeSpan.FromSeconds(15));
		(await generation).Should().Be(0);

		await using ApplicationDbContext verify = fixture.CreateDbContext();
		(await verify.ItemEmbeddings.AnyAsync(row =>
			(row.EntityType == "ItemTemplate" && row.EntityId == templateId)
			|| (row.EntityType == "ReceiptItem" && row.EntityId == itemId))).Should().BeFalse();
		publisher.Verify(p => p.PublishAsync(It.IsAny<CommittedEntityChange>()), Times.Never);
		await verify.ItemEmbeddings
			.Where(row => row.ModelVersion == OnnxEmbeddingService.EmbeddingSpaceFingerprint)
			.ExecuteDeleteAsync();
	}

	private static ItemEmbeddingEntity BaselineEmbedding(string entityType, Guid entityId, string text) => new()
	{
		Id = Guid.NewGuid(),
		EntityType = entityType,
		EntityId = entityId,
		EntityText = text,
		Embedding = new Vector(new float[1024]),
		ModelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint,
		CreatedAt = DateTimeOffset.UtcNow,
	};

	private ServiceProvider BuildWorkerProvider(IEmbeddingService embeddings, IInterceptor interceptor)
	{
		ServiceCollection services = new();
		services.AddSingleton<IEmbeddingService>(embeddings);
		services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(
			new FixtureContextFactory(new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
				.AddInterceptors(interceptor)
				.Options));
		return services.BuildServiceProvider();
	}

	private DbContextOptions<ApplicationDbContext> PurgeOptions(IInterceptor interceptor) =>
		new DbContextOptionsBuilder<ApplicationDbContext>(fixture.CreateOptions())
			.AddInterceptors(interceptor)
			.Options;

	private sealed class HeldEmbeddingService : IEmbeddingService
	{
		public bool IsConfigured => true;
		public TaskCompletionSource<IReadOnlyList<string>> Started { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public async Task<List<float[]>> GenerateEmbeddingsAsync(
			List<string> texts,
			CancellationToken cancellationToken)
		{
			Started.TrySetResult(texts);
			await Release.Task.WaitAsync(cancellationToken);
			return texts.Select(_ => new float[1024]).ToList();
		}
	}

	private sealed class ReceiptDeleteGate : DbCommandInterceptor
	{
		public TaskCompletionSource LockAcquired { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override async ValueTask<int> NonQueryExecutedAsync(
			DbCommand command,
			CommandExecutedEventData eventData,
			int result,
			CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("DELETE FROM receipts.\"ReceiptItems\"", StringComparison.Ordinal))
			{
				LockAcquired.TrySetResult();
				await Release.Task.WaitAsync(cancellationToken);
			}

			return result;
		}
	}

	private sealed class WorkerLockBarrier : DbCommandInterceptor
	{
		public TaskCompletionSource TemplateLockCompleted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReceiptLockAttempted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
			DbCommand command,
			CommandEventData eventData,
			InterceptionResult<DbDataReader> result,
			CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("receipts.\"ReceiptItems\"", StringComparison.Ordinal)
				&& command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
			{
				ReceiptLockAttempted.TrySetResult();
			}

			return ValueTask.FromResult(result);
		}

		public override ValueTask<DbDataReader> ReaderExecutedAsync(
			DbCommand command,
			CommandExecutedEventData eventData,
			DbDataReader result,
			CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains("library.\"ItemTemplates\"", StringComparison.Ordinal)
				&& command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
			{
				TemplateLockCompleted.TrySetResult();
			}

			return ValueTask.FromResult(result);
		}
	}

	private sealed class FixtureContextFactory(DbContextOptions<ApplicationDbContext> options)
		: IDbContextFactory<ApplicationDbContext>
	{
		public ApplicationDbContext CreateDbContext() => new(options);
	}
}
