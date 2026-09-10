using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Domain.NormalizedDescriptions;
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
	private const string CanonicalEntityType = "NormalizedDescription";
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
					logger.LogInformation("Generated embeddings for {Count} semantic source rows", processed);
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

		string modelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint;
		DateTimeOffset now = DateTimeOffset.UtcNow;

		List<GeneratedItem> canonical = accepted
			.Where(generated => generated.Item.EntityType == CanonicalEntityType)
			.ToList();
		List<Guid> canonicalIds = canonical.Select(generated => generated.Item.EntityId).ToList();
		Dictionary<Guid, NormalizedDescriptionEntity> canonicalMap = await context.NormalizedDescriptions
			.Where(entity => canonicalIds.Contains(entity.Id))
			.ToDictionaryAsync(entity => entity.Id, cancellationToken);
		foreach (GeneratedItem generated in canonical)
		{
			if (!canonicalMap.TryGetValue(generated.Item.EntityId, out NormalizedDescriptionEntity? entity))
			{
				continue;
			}

			entity.Embedding = new Vector(generated.Embedding);
			entity.EmbeddingModelVersion = modelVersion;
		}

		List<GeneratedItem> items = accepted
			.Where(generated => generated.Item.EntityType != CanonicalEntityType)
			.ToList();
		List<Guid> entityIds = items.Select(p => p.Item.EntityId).ToList();
		Dictionary<(string EntityType, Guid EntityId), ItemEmbeddingEntity> existingMap = await context.ItemEmbeddings
			.Where(e => entityIds.Contains(e.EntityId))
			.ToDictionaryAsync(e => (e.EntityType, e.EntityId), cancellationToken);

		foreach (GeneratedItem generated in items)
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

		// Vectors and their fingerprints are rebuildable projections, not user mutations.
		// Auditing them would serialize 1,024 floats per row and make every model rollout grow
		// the durable audit trail by the size of the entire corpus.
		context.AuditingEnabled = false;
		int changed = await context.SaveChangesAsync(cancellationToken);
		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken);
		}
		if (canonical.Count > 0 && changed > 0)
		{
			await committedChangePublisher.PublishAsync(new(
				CommittedEntityType.NormalizedDescription,
				CommittedChangeType.Updated,
				SuppressToast: true));
		}
		if (items.Count > 0 && changed > 0)
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

		Guid[] canonicalIds = pending.Where(item => item.EntityType == CanonicalEntityType)
			.Select(item => item.EntityId).Distinct().Order().ToArray();
		if (canonicalIds.Length > 0)
		{
			await context.Database.SqlQuery<Guid>($"""
				SELECT "Id" AS "Value" FROM matching."NormalizedDescriptions"
				WHERE "Id" = ANY ({canonicalIds})
				ORDER BY "Id" FOR UPDATE
				""").ToListAsync(cancellationToken);
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

		Guid[] canonicalIds = pending.Where(item => item.EntityType == CanonicalEntityType)
			.Select(item => item.EntityId).Distinct().ToArray();
		Dictionary<Guid, string> canonicalTexts = await context.NormalizedDescriptions.AsNoTracking()
			.Where(item => canonicalIds.Contains(item.Id) && item.Status != NormalizedDescriptionStatus.Rejected)
			.ToDictionaryAsync(item => item.Id, item => item.CanonicalName, cancellationToken);

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
			Dictionary<Guid, string> currentTexts = item.EntityType switch
			{
				CanonicalEntityType => canonicalTexts,
				"ItemTemplate" => templateTexts,
				_ => receiptItemTexts,
			};
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
		string modelVersion = OnnxEmbeddingService.EmbeddingSpaceFingerprint;

		// Canonical rows drive semantic classification, so restore them first. The model
		// fingerprint comparison makes the queue resumable after either a restore or an
		// embedding-space change without discarding durable names, statuses, or links.
		List<PendingItem> pending = await context.NormalizedDescriptions
			.AsNoTracking()
			.Where(entity =>
				entity.Status != NormalizedDescriptionStatus.Rejected
				&& entity.CanonicalName != string.Empty
				&& (entity.Embedding == null || entity.EmbeddingModelVersion != modelVersion))
			.OrderBy(entity => entity.Id)
			.Select(entity => new PendingItem(CanonicalEntityType, entity.Id, entity.CanonicalName))
			.Take(BatchSize)
			.ToListAsync(cancellationToken);

		int remaining = BatchSize - pending.Count;
		if (remaining <= 0)
		{
			return pending;
		}

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
			.Where(x => x.Embedding == null
				|| x.Embedding.EntityText != x.Template.Name
				|| x.Embedding.ModelVersion != modelVersion)
			.OrderBy(x => x.Template.Id)
			.Select(x => new PendingItem("ItemTemplate", x.Template.Id, x.Template.Name))
			.Take(remaining)
			.ToListAsync(cancellationToken);

		pending.AddRange(templateItems);
		remaining = BatchSize - pending.Count;
		if (remaining <= 0)
		{
			return pending;
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
			.Where(x => x.Embedding == null
				|| x.Embedding.EntityText != x.ReceiptItem.Description
				|| x.Embedding.ModelVersion != modelVersion)
			.OrderBy(x => x.ReceiptItem.Id)
			.Select(x => new PendingItem("ReceiptItem", x.ReceiptItem.Id, x.ReceiptItem.Description))
			.Take(remaining)
			.ToListAsync(cancellationToken);

		pending.AddRange(receiptItems);
		return pending;
	}

	private sealed record PendingItem(string EntityType, Guid EntityId, string Text);
	private sealed record GeneratedItem(PendingItem Item, float[] Embedding);
}
