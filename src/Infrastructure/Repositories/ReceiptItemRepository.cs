using System.Linq.Expressions;
using Application.Models;
using Application.Queries.Core.ReceiptItem.GetReceiptItemSuggestions;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.Extensions;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Services;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructure.Repositories;

public class ReceiptItemRepository(IDbContextFactory<ApplicationDbContext> contextFactory, Microsoft.Extensions.Options.IOptions<Application.Taxonomy.ClassificationOptions>? classificationOptions = null) : IReceiptItemRepository
{
	private static readonly Dictionary<string, Expression<Func<ReceiptItemEntity, object>>> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
	{
		["description"] = e => e.Description,
		["quantity"] = e => e.Quantity,
		["unitPrice"] = e => e.UnitPrice,
		["totalAmount"] = e => e.TotalAmount,
		["category"] = e => e.Category,
	};

	public async Task<ReceiptItemEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		ReceiptItemEntity? item = await context.ReceiptItems.FindAsync([id], cancellationToken);
		if (item is not null) { await CurrentClassificationReader.PopulateAsync(context, [item], cancellationToken); }
		return item;
	}

	public async Task<List<ReceiptItemEntity>> GetByReceiptIdAsync(Guid receiptId, int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		List<ReceiptItemEntity> items = await context.ReceiptItems
			.IgnoreAutoIncludes()
			.Where(ri => ri.ReceiptId == receiptId)
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Description, e => e.Id)
			.Skip(offset)
			.Take(limit)
			.Select(ProjectListRow(context))
			.ToListAsync(cancellationToken);
		await CurrentClassificationReader.PopulateAsync(context, items, cancellationToken);
		return items;
	}

	public async Task<int> GetByReceiptIdCountAsync(Guid receiptId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.ReceiptItems
			.Where(ri => ri.ReceiptId == receiptId)
			.CountAsync(cancellationToken);
	}

	public Task<List<ReceiptItemEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
		=> GetAllAsync(offset, limit, sort, q: null, normalizedDescriptionId: null, cancellationToken);

	public Task<List<ReceiptItemEntity>> GetAllAsync(int offset, int limit, SortParams sort, string? q, CancellationToken cancellationToken)
		=> GetAllAsync(offset, limit, sort, q, normalizedDescriptionId: null, cancellationToken);

	public async Task<List<ReceiptItemEntity>> GetAllAsync(
		int offset,
		int limit,
		SortParams sort,
		string? q,
		Guid? normalizedDescriptionId,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<ReceiptItemEntity> query = context.ReceiptItems
			.IgnoreAutoIncludes()
			.AsNoTracking();
		query = ApplyFilters(query, q, normalizedDescriptionId);
		List<ReceiptItemEntity> items = await query
			.ApplySort(sort, AllowedSortColumns, e => e.Description, e => e.Id)
			.Skip(offset)
			.Take(limit)
			.Select(ProjectListRow(context))
			.ToListAsync(cancellationToken);
		await CurrentClassificationReader.PopulateAsync(context, items, cancellationToken);
		return items;
	}

	/// <summary>
	/// The shape returned by the list endpoints.
	/// </summary>
	/// <remarks>
	/// The three normalized-description fields were missing here, which made
	/// <c>GET /api/receipt-items</c> return <c>normalizedDescriptionId: null</c> on every row
	/// despite the spec documenting it — and left the review queue's split dialog, which filters
	/// on exactly that field, unable to match anything at all (RECEIPTS-877).
	///
	/// The neighbour is projected as a trimmed stand-in rather than <c>Include</c>d: a full
	/// include would drag the 1024-float embedding across the wire for every row of every page.
	/// </remarks>
	private static Expression<Func<ReceiptItemEntity, ReceiptItemEntity>> ProjectListRow(ApplicationDbContext context) =>
		ri => new ReceiptItemEntity
		{
			Id = ri.Id,
			NormalizationRevision = ri.NormalizationRevision,
			NormalizationReviewState = ri.NormalizationReviewState,
			NormalizationReviewReason = ri.NormalizationReviewReason,
			NormalizationExclusionReason = ri.NormalizationExclusionReason,
			NormalizationReviewDecisionId = ri.NormalizationReviewDecisionId,
			NormalizationSourceHash = ri.NormalizationSourceHash,
			ReviewedSourceHash = ri.ReviewedSourceHash,
			ReviewedTargetId = ri.ReviewedTargetId,
			ReviewedBy = ri.ReviewedBy,
			ReviewedAt = ri.ReviewedAt,
			ReviewMappingRevision = ri.ReviewMappingRevision,
			NormalizationOrigin = ri.NormalizationOrigin,
			NormalizationAssignmentId = ri.NormalizationAssignmentId,
			NormalizationMappingId = ri.NormalizationMappingId,
			ReceiptId = ri.ReceiptId,
			ReceiptItemCode = ri.ReceiptItemCode,
			Description = ri.Description,
			Quantity = ri.Quantity,
			UnitPrice = ri.UnitPrice,
			UnitPriceCurrency = ri.UnitPriceCurrency,
			TotalAmount = ri.TotalAmount,
			TotalAmountCurrency = ri.TotalAmountCurrency,
			Category = ri.Category,
			Subcategory = ri.Subcategory,
			NormalizedDescriptionId = ri.NormalizedDescriptionId,
			NormalizedDescriptionMatchScore = ri.NormalizedDescriptionMatchScore,

			// A correlated subquery rather than a navigation access. The queries above run under
			// IgnoreAutoIncludes, which leaves ri.NormalizedDescription unpopulated — reading it
			// here yields null on some providers regardless of the underlying row. Selecting the
			// two display columns explicitly is provider-agnostic and, unlike an Include, does not
			// drag the 1024-float embedding across the wire for every row of every page.
			NormalizedDescription = context.NormalizedDescriptions
				.Where(n => n.Id == ri.NormalizedDescriptionId)
				.Select(n => new NormalizedDescriptionEntity
				{
					Id = n.Id,
					NormalizationRevision = n.NormalizationRevision,
					CanonicalName = n.CanonicalName,
					DisplayLabel = n.DisplayLabel,
					Status = n.Status,
				})
				.FirstOrDefault(),
		};

	private static IQueryable<ReceiptItemEntity> ApplyFilters(
		IQueryable<ReceiptItemEntity> query,
		string? q,
		Guid? normalizedDescriptionId)
	{
		if (normalizedDescriptionId.HasValue)
		{
			// Backed by IX_ReceiptItems_NormalizedDescriptionId, so this is an index seek rather
			// than the client-side scan over a fixed page the split dialog used to attempt.
			query = query.Where(ri => ri.NormalizedDescriptionId == normalizedDescriptionId.Value);
		}

		return ApplySearchFilter(query, q);
	}

	private static IQueryable<ReceiptItemEntity> ApplySearchFilter(IQueryable<ReceiptItemEntity> query, string? q)
	{
		if (string.IsNullOrWhiteSpace(q))
		{
			return query;
		}

		string pattern = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
		return query.Where(ri =>
			EF.Functions.ILike(ri.Description, pattern, @"\")
			|| (ri.ReceiptItemCode != null && EF.Functions.ILike(ri.ReceiptItemCode, pattern, @"\"))
			|| EF.Functions.ILike(ri.Category, pattern, @"\")
			|| (ri.Subcategory != null && EF.Functions.ILike(ri.Subcategory, pattern, @"\")));
	}

	public async Task<List<ReceiptItemEntity>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		List<ReceiptItemEntity> items = await context.ReceiptItems
			.OnlyDeleted()
			.Where(ri => ri.CascadeDeletedByParentId == null)
			.IgnoreAutoIncludes()
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Description, e => e.Id)
			.Select(ri => new ReceiptItemEntity
			{
				Id = ri.Id,
				NormalizationRevision = ri.NormalizationRevision,
				NormalizationReviewState = ri.NormalizationReviewState,
				NormalizationReviewReason = ri.NormalizationReviewReason,
				NormalizationExclusionReason = ri.NormalizationExclusionReason,
				NormalizationReviewDecisionId = ri.NormalizationReviewDecisionId,
				NormalizationSourceHash = ri.NormalizationSourceHash,
				ReviewedSourceHash = ri.ReviewedSourceHash,
				ReviewedTargetId = ri.ReviewedTargetId,
				ReviewedBy = ri.ReviewedBy,
				ReviewedAt = ri.ReviewedAt,
				ReviewMappingRevision = ri.ReviewMappingRevision,
				NormalizationOrigin = ri.NormalizationOrigin,
				NormalizationAssignmentId = ri.NormalizationAssignmentId,
				NormalizationMappingId = ri.NormalizationMappingId,
				ReceiptId = ri.ReceiptId,
				ReceiptItemCode = ri.ReceiptItemCode,
				Description = ri.Description,
				Quantity = ri.Quantity,
				UnitPrice = ri.UnitPrice,
				UnitPriceCurrency = ri.UnitPriceCurrency,
				TotalAmount = ri.TotalAmount,
				TotalAmountCurrency = ri.TotalAmountCurrency,
				Category = ri.Category,
				Subcategory = ri.Subcategory,
				DeletedAt = ri.DeletedAt
			})
			.Skip(offset)
			.Take(limit)
			.ToListAsync(cancellationToken);
		await CurrentClassificationReader.PopulateAsync(context, items, cancellationToken);
		return items;
	}

	public async Task<int> GetDeletedCountAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.ReceiptItems
			.OnlyDeleted()
			.Where(ri => ri.CascadeDeletedByParentId == null)
			.CountAsync(cancellationToken);
	}

	public Task<List<ReceiptItemEntity>> CreateAsync(List<ReceiptItemEntity> entities, CancellationToken cancellationToken)
		=> CreateCoreAsync(entities, [], null, cancellationToken);

	public Task<List<ReceiptItemEntity>> CreateAsync(List<ReceiptItemEntity> entities,
		IReadOnlyList<Guid?> templateIds, CancellationToken cancellationToken)
		=> CreateCoreAsync(entities, templateIds, null, cancellationToken);

	public Task<List<ReceiptItemEntity>> CreateAsync(List<ReceiptItemEntity> entities,
		IReadOnlyList<Guid?> templateIds, Guid clientRequestId, CancellationToken cancellationToken)
		=> CreateCoreAsync(entities, templateIds, clientRequestId, cancellationToken);

	private async Task<List<ReceiptItemEntity>> CreateCoreAsync(List<ReceiptItemEntity> entities,
		IReadOnlyList<Guid?> templateIds, Guid? clientRequestId, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(templateIds);
		if (templateIds.Count != 0 && templateIds.Count != entities.Count)
		{
			throw new ArgumentException("Template IDs must align with receipt items.", nameof(templateIds));
		}

		Guid?[] hints = templateIds.ToArray();
		Guid[] ids = hints.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
		for (int attempt = 0; ; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			using ApplicationDbContext context = contextFactory.CreateDbContext();
			await using IDbContextTransaction? transaction = context.Database.IsRelational()
				? await context.Database.BeginTransactionAsync(cancellationToken) : null;
			await NormalizationWriteGuard.AcquireWriteGateAsync(context, cancellationToken);
			ClassificationService.RequireRequestId(entities, clientRequestId);
			await ClassificationService.PrepareAggregateAsync(context, entities, classificationOptions?.Value.WritesEnabled == true, cancellationToken);
			ReceiptWriteRequestEntity? request = null;
			if (clientRequestId is Guid requestId)
			{
				request = await Infrastructure.Services.ReceiptWriteIdempotency.FindOrCreateAsync(context, requestId, "item-create",
					new { ReceiptId = entities[0].ReceiptId, templates = templateIds, items = entities.Select(value => new { value.Description, value.ReceiptItemCode, value.Quantity, value.UnitPrice, value.TotalAmount, value.UnitPriceCurrency, value.TotalAmountCurrency, value.Category, value.Subcategory, value.NormalizationIntent, value.Classification }) }, cancellationToken);
				if (request.ReceiptId != Guid.Empty)
				{
					var saved = await context.ReceiptItems.Where(value => request.ItemIds.Contains(value.Id)).ToListAsync(cancellationToken);
					if (saved.Count != request.ItemIds.Length) { throw new Application.Normalization.NormalizationCatalogException(409, "receipt_request_deleted", "Items created by this request are no longer live. Restore them instead of repeating creation."); }
					await CurrentClassificationReader.PopulateAsync(context, saved, cancellationToken);
					return request.ItemIds.Select(id => saved.Single(value => value.Id == id)).ToList();
				}
			}
			List<Guid> targetList = await context.ItemTemplates.AsNoTracking()
				.Where(template => ids.Contains(template.Id) && template.NormalizedDescriptionId != null)
				.Select(template => template.NormalizedDescriptionId!.Value).Distinct().ToListAsync(cancellationToken);
			Guid[] targetIds = targetList.ToArray();
			await NormalizationWriteGuard.LockTargetsAsync(context, targetIds, cancellationToken);
			await TemplateWriteGuard.LockTemplatesAsync(context, ids, cancellationToken);

			// A nonparticipating writer may have changed the FK while we waited for a template.
			// Read it again, and retry without reversing the canonical→template lock order.
			Dictionary<Guid, ItemTemplateEntity> templates = await context.ItemTemplates
				.Where(template => ids.Contains(template.Id)).ToDictionaryAsync(template => template.Id, cancellationToken);
			bool targetsChanged = templates.Values.Any(template => template.NormalizedDescriptionId is { } targetId
				&& !targetIds.Contains(targetId));
			if (targetsChanged && attempt < 2)
			{
				continue;
			}

			Dictionary<Guid, NormalizedDescriptionEntity> targets = await context.NormalizedDescriptions
				.Where(target => targetIds.Contains(target.Id) && target.Status != NormalizedDescriptionStatus.Rejected)
				.ToDictionaryAsync(target => target.Id, cancellationToken);
			for (int i = 0; i < entities.Count; i++)
			{
				ReceiptItemEntity entity = entities[i];
				entity.NormalizedDescriptionId = null;
				entity.NormalizedDescriptionMatchScore = null;
				if (i < hints.Length && hints[i] is { } templateId && templates.TryGetValue(templateId, out ItemTemplateEntity? template)
					&& template.NormalizedDescriptionId is { } targetId && targets.ContainsKey(targetId))
				{
					entity.NormalizedDescriptionId = targetId;
					entity.NormalizationOrigin = "template";
					entity.NormalizationAssignmentId = Guid.NewGuid();
				}
			}

			// Stale/unknown/deleted/unlinked hints are convenience fallbacks. Even repeated
			// concurrent relinking must not prevent the receipt itself from being saved.
			context.ReceiptItems.AddRange(entities);
			if (request is not null) { request.ReceiptId = entities[0].ReceiptId; request.ItemIds = entities.Select(value => value.Id).ToArray(); }
			await context.SaveChangesAsync(cancellationToken);
			await ClassificationService.ApplyAggregateAsync(context, entities, classificationOptions?.Value.WritesEnabled == true, cancellationToken);
			await CurrentClassificationReader.PopulateAsync(context, entities, cancellationToken);
			if (transaction is not null)
			{
				await transaction.CommitAsync(cancellationToken);
			}
			return entities;
		}
	}

	public Task UpdateAsync(List<ReceiptItemEntity> entities, CancellationToken cancellationToken)
		=> UpdateCoreAsync(entities, null, cancellationToken);

	public Task UpdateAsync(List<ReceiptItemEntity> entities, Guid clientRequestId, CancellationToken cancellationToken)
		=> UpdateCoreAsync(entities, clientRequestId, cancellationToken);

	private async Task UpdateCoreAsync(List<ReceiptItemEntity> entities, Guid? clientRequestId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		await using IDbContextTransaction? transaction = context.Database.IsRelational()
			? await context.Database.BeginTransactionAsync(cancellationToken) : null;
		Guid[] ids = entities.Select(entity => entity.Id).Distinct().ToArray();
		// Read after locking: a worker may otherwise fill originally-null metadata before
		// this edit saves, causing EF to omit its null clears and audit stale original values.
		// Edits only preserve or clear links, so they need item locks, not canonical locks.
		await NormalizationWriteGuard.AcquireWriteGateAsync(context, cancellationToken);
		ClassificationService.RequireRequestId(entities, clientRequestId);
		await ClassificationService.PrepareAggregateAsync(context, entities, classificationOptions?.Value.WritesEnabled == true, cancellationToken);
		if (clientRequestId is Guid requestId)
		{
			var request = await Infrastructure.Services.ReceiptWriteIdempotency.FindOrCreateAsync(context, requestId, "item-update",
				entities.Select(value => new
				{
					value.Id,
					value.ReceiptId,
					value.Description,
					value.ReceiptItemCode,
					value.Quantity,
					value.UnitPrice,
					value.UnitPriceCurrency,
					value.TotalAmount,
					value.TotalAmountCurrency,
					value.Category,
					value.Subcategory,
					value.NormalizationIntent,
					value.Classification
				}), cancellationToken);
			if (request.ReceiptId != Guid.Empty) { return; }
			request.ReceiptId = entities[0].ReceiptId;
			request.ItemIds = ids;
		}
		await ReceiptFactLock.AcquireAsync(context, await context.ReceiptItems.IgnoreAutoIncludes().Where(x => ids.Contains(x.Id)).Select(x => x.ReceiptId).Distinct().ToListAsync(cancellationToken), cancellationToken);
		await NormalizationWriteGuard.LockItemsAsync(context, ids, cancellationToken);
		List<ReceiptItemEntity> existingEntities = await context.ReceiptItems
			.IgnoreAutoIncludes()
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		foreach (ReceiptItemEntity entity in entities)
		{
			ReceiptItemEntity existingEntity = existingEntities.Single(e => e.Id == entity.Id);
			await ClassificationService.ProtectLegacyEditAsync(context, entity, existingEntity, cancellationToken);
			// Canonical identity describes the raw text. Ordinary edits preserve it; a text
			// change invalidates both fields so the resolver can classify the new description.
			if (!string.Equals(existingEntity.Description, entity.Description, StringComparison.Ordinal))
			{
				existingEntity.NormalizedDescriptionId = null;
				existingEntity.NormalizedDescriptionMatchScore = null;
			}

			existingEntity.NormalizationIntent = entity.NormalizationIntent;

			// Parent identity and deletion metadata are not writable through an item edit.
			existingEntity.ReceiptItemCode = entity.ReceiptItemCode;
			existingEntity.Description = entity.Description;
			existingEntity.Quantity = entity.Quantity;
			existingEntity.UnitPrice = entity.UnitPrice;
			existingEntity.UnitPriceCurrency = entity.UnitPriceCurrency;
			existingEntity.TotalAmount = entity.TotalAmount;
			existingEntity.TotalAmountCurrency = entity.TotalAmountCurrency;
			existingEntity.Category = entity.Category;
			existingEntity.Subcategory = entity.Subcategory;
		}

		await context.SaveChangesAsync(cancellationToken);
		await ClassificationService.ApplyAggregateAsync(context, entities, classificationOptions?.Value.WritesEnabled == true, cancellationToken);
		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken);
		}
	}

	public async Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		List<ReceiptItemEntity> entities = await context.ReceiptItems
			.IgnoreAutoIncludes()
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		context.ReceiptItems.RemoveRange(entities);
		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.ReceiptItems.AnyAsync(e => e.Id == id, cancellationToken);
	}

	public Task<int> GetCountAsync(CancellationToken cancellationToken)
		=> GetCountAsync(q: null, normalizedDescriptionId: null, cancellationToken);

	public Task<int> GetCountAsync(string? q, CancellationToken cancellationToken)
		=> GetCountAsync(q, normalizedDescriptionId: null, cancellationToken);

	public async Task<int> GetCountAsync(string? q, Guid? normalizedDescriptionId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<ReceiptItemEntity> query = context.ReceiptItems.IgnoreAutoIncludes().AsNoTracking();
		query = ApplyFilters(query, q, normalizedDescriptionId);
		return await query.CountAsync(cancellationToken);
	}

	public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		ReceiptItemEntity? entity = await context.ReceiptItems
			.IncludeDeleted()
			.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt != null, cancellationToken);

		if (entity is null)
		{
			return false;
		}

		entity.DeletedAt = null;
		entity.DeletedByUserId = null;
		entity.DeletedByApiKeyId = null;
		entity.CascadeDeletedByParentId = null;
		await context.SaveChangesAsync(cancellationToken);
		return true;
	}

	public async Task<List<ReceiptItemSuggestion>> GetSuggestionsAsync(string itemCode, string? location, int limit, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		string lowerItemCode = itemCode.ToLowerInvariant();

		// Try location-filtered results first
		if (!string.IsNullOrWhiteSpace(location))
		{
			string lowerLocation = location.ToLowerInvariant();

			List<ReceiptItemSuggestion> locationResults = await context.ReceiptItems
				.AsNoTracking()
				.Where(ri => ri.ReceiptItemCode != null && ri.ReceiptItemCode != "")
				.Where(ri => ri.Receipt != null && ri.Receipt.Location.ToLower() == lowerLocation)
				.Where(ri => ri.ReceiptItemCode!.ToLower().Contains(lowerItemCode))
				.GroupBy(ri => ri.ReceiptItemCode!.ToLower())
				.OrderByDescending(g => g.Count())
				.Select(g => new ReceiptItemSuggestion
				{
					ItemCode = g.OrderByDescending(ri => ri.Id).First().ReceiptItemCode!,
					Description = g.OrderByDescending(ri => ri.Id).First().Description,
					Category = g.OrderByDescending(ri => ri.Id).First().Category,
					Subcategory = g.OrderByDescending(ri => ri.Id).First().Subcategory,
					UnitPrice = g.OrderByDescending(ri => ri.Id).First().UnitPrice,
					MatchType = "location",
				})
				.Take(limit)
				.ToListAsync(cancellationToken);

			if (locationResults.Count > 0)
			{
				return locationResults;
			}
		}

		// Fall back to all-location matches
		List<ReceiptItemSuggestion> globalResults = await context.ReceiptItems
			.AsNoTracking()
			.Where(ri => ri.ReceiptItemCode != null && ri.ReceiptItemCode != "")
			.Where(ri => ri.ReceiptItemCode!.ToLower().Contains(lowerItemCode))
			.GroupBy(ri => ri.ReceiptItemCode!.ToLower())
			.OrderByDescending(g => g.Count())
			.Select(g => new ReceiptItemSuggestion
			{
				ItemCode = g.OrderByDescending(ri => ri.Id).First().ReceiptItemCode!,
				Description = g.OrderByDescending(ri => ri.Id).First().Description,
				Category = g.OrderByDescending(ri => ri.Id).First().Category,
				Subcategory = g.OrderByDescending(ri => ri.Id).First().Subcategory,
				UnitPrice = g.OrderByDescending(ri => ri.Id).First().UnitPrice,
				MatchType = "global",
			})
			.Take(limit)
			.ToListAsync(cancellationToken);

		return globalResults;
	}
}
