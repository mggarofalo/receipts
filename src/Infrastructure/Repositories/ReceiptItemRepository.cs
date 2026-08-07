using System.Linq.Expressions;
using Application.Models;
using Application.Queries.Core.ReceiptItem.GetReceiptItemSuggestions;
using Infrastructure.Entities.Core;
using Infrastructure.Extensions;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class ReceiptItemRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : IReceiptItemRepository
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
		return await context.ReceiptItems.FindAsync([id], cancellationToken);
	}

	public async Task<List<ReceiptItemEntity>> GetByReceiptIdAsync(Guid receiptId, int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.ReceiptItems
			.IgnoreAutoIncludes()
			.Where(ri => ri.ReceiptId == receiptId)
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Description, e => e.Id)
			.Skip(offset)
			.Take(limit)
			.Select(ProjectListRow(context))
			.ToListAsync(cancellationToken);
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
		return await query
			.ApplySort(sort, AllowedSortColumns, e => e.Description, e => e.Id)
			.Skip(offset)
			.Take(limit)
			.Select(ProjectListRow(context))
			.ToListAsync(cancellationToken);
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
			EF.Functions.ILike(ri.Description, pattern)
			|| (ri.ReceiptItemCode != null && EF.Functions.ILike(ri.ReceiptItemCode, pattern))
			|| EF.Functions.ILike(ri.Category, pattern)
			|| (ri.Subcategory != null && EF.Functions.ILike(ri.Subcategory, pattern)));
	}

	public async Task<List<ReceiptItemEntity>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.ReceiptItems
			.OnlyDeleted()
			.Where(ri => ri.CascadeDeletedByParentId == null)
			.IgnoreAutoIncludes()
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Description, e => e.Id)
			.Select(ri => new ReceiptItemEntity
			{
				Id = ri.Id,
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
	}

	public async Task<int> GetDeletedCountAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.ReceiptItems
			.OnlyDeleted()
			.Where(ri => ri.CascadeDeletedByParentId == null)
			.CountAsync(cancellationToken);
	}

	public async Task<List<ReceiptItemEntity>> CreateAsync(List<ReceiptItemEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		context.ReceiptItems.AddRange(entities);
		await context.SaveChangesAsync(cancellationToken);
		return entities;
	}

	public async Task UpdateAsync(List<ReceiptItemEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IEnumerable<Guid> ids = entities.Select(e => e.Id);
		List<ReceiptItemEntity> existingEntities = await context.ReceiptItems
			.IgnoreAutoIncludes()
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		foreach (ReceiptItemEntity entity in entities)
		{
			ReceiptItemEntity existingEntity = existingEntities.Single(e => e.Id == entity.Id);
			context.Entry(existingEntity).CurrentValues.SetValues(entity);
		}

		await context.SaveChangesAsync(cancellationToken);
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
