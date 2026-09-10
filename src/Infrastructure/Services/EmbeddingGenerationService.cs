using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace Infrastructure.Services;

public class EmbeddingGenerationService(
	IServiceScopeFactory scopeFactory,
	ILogger<EmbeddingGenerationService> logger,
	ICommittedChangePublisher committedChangePublisher) : BackgroundService
{
	public EmbeddingGenerationService(
		IServiceScopeFactory scopeFactory,
		ILogger<EmbeddingGenerationService> logger)
		: this(scopeFactory, logger, new NullCommittedChangePublisher())
	{
	}

	private const int BatchSize = 50;
	private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try
		{
			await Task.Delay(InitialDelay, stoppingToken);
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
			return;
		}

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				int processed = await ProcessPendingEmbeddingsAsync(stoppingToken);
				if (processed > 0)
				{
					logger.LogInformation("Generated embeddings for {Count} items", processed);
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Error during embedding generation cycle");
			}

			try
			{
				await Task.Delay(Interval, stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
		}
	}

	internal async Task<int> ProcessPendingEmbeddingsAsync(CancellationToken cancellationToken)
	{
		using IServiceScope scope = scopeFactory.CreateScope();
		IEmbeddingService embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
		IDbContextFactory<ApplicationDbContext> contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

		if (!embeddingService.IsConfigured)
		{
			return 0;
		}

		using ApplicationDbContext context = contextFactory.CreateDbContext();

		List<PendingItem> pending = await GetPendingItemsAsync(context, cancellationToken);
		if (pending.Count == 0)
		{
			return 0;
		}

		List<string> texts = pending.Select(p => p.Text).ToList();
		List<float[]> embeddings = await embeddingService.GenerateEmbeddingsAsync(texts, cancellationToken);

		// Inference can be slow, so keep it outside the transaction. The short write phase
		// locks sources in one stable order and rereads their text; a rename or delete that
		// committed during inference therefore cannot leave an old-text vector under the ID.
		await using IDbContextTransaction? transaction = context.Database.IsRelational()
			? await context.Database.BeginTransactionAsync(cancellationToken) : null;
		await LockPendingSourcesAsync(context, pending, cancellationToken);
		List<GeneratedItem> accepted = await KeepCurrentSourcesAsync(context, pending, embeddings, cancellationToken);
		if (accepted.Count == 0)
		{
			return 0;
		}

		string modelVersion = OnnxEmbeddingService.ModelName;
		DateTimeOffset now = DateTimeOffset.UtcNow;

		List<Guid> entityIds = accepted.Select(p => p.Item.EntityId).ToList();
		Dictionary<(string EntityType, Guid EntityId), ItemEmbeddingEntity> existingMap = await context.ItemEmbeddings
			.Where(e => entityIds.Contains(e.EntityId))
			.ToDictionaryAsync(e => (e.EntityType, e.EntityId), cancellationToken);

		foreach (GeneratedItem generated in accepted)
		{
			PendingItem item = generated.Item;

			if (existingMap.TryGetValue((item.EntityType, item.EntityId), out ItemEmbeddingEntity? existing))
			{
				existing.EntityText = item.Text;
				existing.Embedding = new Vector(generated.Embedding);
				existing.ModelVersion = modelVersion;
				existing.CreatedAt = now;
			}
			else
			{
				context.ItemEmbeddings.Add(new ItemEmbeddingEntity
				{
					Id = Guid.NewGuid(),
					EntityType = item.EntityType,
					EntityId = item.EntityId,
					EntityText = item.Text,
					Embedding = new Vector(generated.Embedding),
					ModelVersion = modelVersion,
					CreatedAt = now,
				});
			}
		}

		int changed = await context.SaveChangesAsync(cancellationToken);
		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken);
		}
		if (changed > 0)
		{
			await committedChangePublisher.PublishAsync(new(
				CommittedEntityType.ItemEmbedding,
				CommittedChangeType.Updated,
				SuppressToast: true));
		}
		return accepted.Count;
	}

	private static async Task LockPendingSourcesAsync(
		ApplicationDbContext context,
		List<PendingItem> pending,
		CancellationToken cancellationToken)
	{
		if (!context.Database.IsNpgsql())
		{
			return;
		}

		Guid[] templateIds = pending.Where(item => item.EntityType == "ItemTemplate")
			.Select(item => item.EntityId).Distinct().Order().ToArray();
		if (templateIds.Length > 0)
		{
			await context.Database.SqlQuery<Guid>($"""
				SELECT "Id" AS "Value" FROM library."ItemTemplates"
				WHERE "Id" = ANY ({templateIds}) AND "DeletedAt" IS NULL
				ORDER BY "Id" FOR UPDATE
				""").ToListAsync(cancellationToken);
		}

		Guid[] receiptItemIds = pending.Where(item => item.EntityType == "ReceiptItem")
			.Select(item => item.EntityId).Distinct().Order().ToArray();
		if (receiptItemIds.Length > 0)
		{
			await context.Database.SqlQuery<Guid>($"""
				SELECT "Id" AS "Value" FROM receipts."ReceiptItems"
				WHERE "Id" = ANY ({receiptItemIds}) AND "DeletedAt" IS NULL
				ORDER BY "Id" FOR UPDATE
				""").ToListAsync(cancellationToken);
		}
	}

	private static async Task<List<GeneratedItem>> KeepCurrentSourcesAsync(
		ApplicationDbContext context,
		List<PendingItem> pending,
		List<float[]> embeddings,
		CancellationToken cancellationToken)
	{
		if (embeddings.Count != pending.Count)
		{
			throw new InvalidOperationException("Embedding provider returned a result count that does not match the requested batch.");
		}

		Guid[] templateIds = pending.Where(item => item.EntityType == "ItemTemplate")
			.Select(item => item.EntityId).Distinct().ToArray();
		Dictionary<Guid, string> templateTexts = await context.ItemTemplates.IgnoreQueryFilters().AsNoTracking()
			.Where(item => templateIds.Contains(item.Id) && item.DeletedAt == null)
			.ToDictionaryAsync(item => item.Id, item => item.Name, cancellationToken);

		Guid[] receiptItemIds = pending.Where(item => item.EntityType == "ReceiptItem")
			.Select(item => item.EntityId).Distinct().ToArray();
		Dictionary<Guid, string> receiptItemTexts = await context.ReceiptItems.IgnoreQueryFilters()
			.IgnoreAutoIncludes().AsNoTracking()
			.Where(item => receiptItemIds.Contains(item.Id) && item.DeletedAt == null)
			.ToDictionaryAsync(item => item.Id, item => item.Description, cancellationToken);

		List<GeneratedItem> accepted = [];
		for (int i = 0; i < pending.Count; i++)
		{
			PendingItem item = pending[i];
			Dictionary<Guid, string> currentTexts = item.EntityType == "ItemTemplate"
				? templateTexts : receiptItemTexts;
			if (currentTexts.TryGetValue(item.EntityId, out string? currentText)
				&& string.Equals(currentText, item.Text, StringComparison.Ordinal))
			{
				accepted.Add(new(item, embeddings[i]));
			}
		}
		return accepted;
	}

	private static async Task<List<PendingItem>> GetPendingItemsAsync(ApplicationDbContext context, CancellationToken cancellationToken)
	{
		// Find ItemTemplates without embeddings or with stale text
		List<PendingItem> templateItems = await context.ItemTemplates
			.IgnoreQueryFilters()
			.Where(t => t.DeletedAt == null && t.Name.Length >= 2)
			.GroupJoin(
				context.ItemEmbeddings.Where(e => e.EntityType == "ItemTemplate"),
				t => t.Id,
				e => e.EntityId,
				(t, embeddings) => new { Template = t, Embeddings = embeddings })
			.SelectMany(
				x => x.Embeddings.DefaultIfEmpty(),
				(x, e) => new { x.Template, Embedding = e })
			.Where(x => x.Embedding == null || x.Embedding.EntityText != x.Template.Name)
			.OrderBy(x => x.Template.Id)
			.Select(x => new PendingItem("ItemTemplate", x.Template.Id, x.Template.Name))
			.Take(BatchSize)
			.ToListAsync(cancellationToken);

		int remaining = BatchSize - templateItems.Count;
		if (remaining <= 0)
		{
			return templateItems;
		}

		// Find ReceiptItems without embeddings or with stale text
		List<PendingItem> receiptItems = await context.ReceiptItems
			.IgnoreQueryFilters()
			.Where(r => r.DeletedAt == null && r.Description.Length >= 2)
			.GroupJoin(
				context.ItemEmbeddings.Where(e => e.EntityType == "ReceiptItem"),
				r => r.Id,
				e => e.EntityId,
				(r, embeddings) => new { ReceiptItem = r, Embeddings = embeddings })
			.SelectMany(
				x => x.Embeddings.DefaultIfEmpty(),
				(x, e) => new { x.ReceiptItem, Embedding = e })
			.Where(x => x.Embedding == null || x.Embedding.EntityText != x.ReceiptItem.Description)
			.OrderBy(x => x.ReceiptItem.Id)
			.Select(x => new PendingItem("ReceiptItem", x.ReceiptItem.Id, x.ReceiptItem.Description))
			.Take(remaining)
			.ToListAsync(cancellationToken);

		templateItems.AddRange(receiptItems);
		return templateItems;
	}

	private sealed record PendingItem(string EntityType, Guid EntityId, string Text);
	private sealed record GeneratedItem(PendingItem Item, float[] Embedding);
}
