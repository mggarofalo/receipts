using System.Linq.Expressions;
using Application.Models;
using Infrastructure.Entities.Core;
using Infrastructure.Extensions;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class SubcategoryRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : ISubcategoryRepository
{
	private static readonly Dictionary<string, Expression<Func<SubcategoryEntity, object>>> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
	{
		["name"] = e => e.Name,
	};

	public async Task<SubcategoryEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Subcategories.FindAsync([id], cancellationToken);
	}

	public async Task<List<SubcategoryEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken, bool? isActive = null, string? q = null)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<SubcategoryEntity> query = context.Subcategories.AsNoTracking();
		if (isActive.HasValue)
		{
			query = query.Where(e => e.IsActive == isActive.Value);
		}
		query = ApplySearchFilter(query, q);

		return await query
			.ApplySort(sort, AllowedSortColumns, e => e.Name, e => e.Id)
			.Skip(offset)
			.Take(limit)
			.ToListAsync(cancellationToken);
	}

	public async Task<List<SubcategoryEntity>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Subcategories
			.OnlyDeleted()
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Name, e => e.Id)
			.Select(s => new SubcategoryEntity
			{
				Id = s.Id,
				Name = s.Name,
				CategoryId = s.CategoryId,
				Description = s.Description,
				IsActive = s.IsActive,
				DeletedAt = s.DeletedAt
			})
			.Skip(offset)
			.Take(limit)
			.ToListAsync(cancellationToken);
	}

	public async Task<int> GetDeletedCountAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Subcategories
			.OnlyDeleted()
			.CountAsync(cancellationToken);
	}

	public async Task<List<SubcategoryEntity>> GetByCategoryIdAsync(Guid categoryId, int offset, int limit, SortParams sort, CancellationToken cancellationToken, bool? isActive = null, string? q = null)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<SubcategoryEntity> query = context.Subcategories
			.Where(s => s.CategoryId == categoryId);
		if (isActive.HasValue)
		{
			query = query.Where(e => e.IsActive == isActive.Value);
		}
		query = ApplySearchFilter(query, q);

		return await query
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Name, e => e.Id)
			.Skip(offset)
			.Take(limit)
			.ToListAsync(cancellationToken);
	}

	public async Task<int> GetByCategoryIdCountAsync(Guid categoryId, CancellationToken cancellationToken, bool? isActive = null, string? q = null)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<SubcategoryEntity> query = context.Subcategories
			.Where(s => s.CategoryId == categoryId);
		if (isActive.HasValue)
		{
			query = query.Where(e => e.IsActive == isActive.Value);
		}
		query = ApplySearchFilter(query, q);

		return await query.CountAsync(cancellationToken);
	}

	public async Task<List<SubcategoryEntity>> CreateAsync(List<SubcategoryEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		context.Subcategories.AddRange(entities);
		await context.SaveChangesAsync(cancellationToken);
		return entities;
	}

	public async Task UpdateAsync(List<SubcategoryEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IEnumerable<Guid> ids = entities.Select(e => e.Id);
		List<SubcategoryEntity> existingEntities = await context.Subcategories
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		foreach (SubcategoryEntity entity in entities)
		{
			SubcategoryEntity existingEntity = existingEntities.Single(e => e.Id == entity.Id);
			context.Entry(existingEntity).CurrentValues.SetValues(entity);
		}

		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Subcategories.AnyAsync(e => e.Id == id, cancellationToken);
	}

	public async Task<int> GetCountAsync(CancellationToken cancellationToken, bool? isActive = null, string? q = null)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<SubcategoryEntity> query = context.Subcategories;
		if (isActive.HasValue)
		{
			query = query.Where(e => e.IsActive == isActive.Value);
		}
		query = ApplySearchFilter(query, q);

		return await query.CountAsync(cancellationToken);
	}

	private static IQueryable<SubcategoryEntity> ApplySearchFilter(IQueryable<SubcategoryEntity> query, string? q)
	{
		if (string.IsNullOrWhiteSpace(q))
		{
			return query;
		}
		string search = q.Trim().ToLower();
		return query.Where(e => e.Name.ToLower().Contains(search));
	}

	public async Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		List<SubcategoryEntity> entities = await context.Subcategories
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		context.Subcategories.RemoveRange(entities);
		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		SubcategoryEntity? entity = await context.Subcategories
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

	public async Task<string?> GetRestoreConflictNameAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();

		// The soft-deleted row the caller intends to restore.
		SubcategoryEntity? deleted = await context.Subcategories
			.IncludeDeleted()
			.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt != null, cancellationToken);

		if (deleted is null)
		{
			// Nothing to restore (absent or already active) — no conflict to report.
			return null;
		}

		// context.Subcategories is filtered to active rows (DeletedAt == null). A match on the
		// natural key (CategoryId, Name) means an active subcategory already occupies it, so
		// restoring would violate the filtered unique index. Return the colliding name.
		bool conflict = await context.Subcategories
			.AnyAsync(e => e.CategoryId == deleted.CategoryId && e.Name == deleted.Name, cancellationToken);

		return conflict ? deleted.Name : null;
	}

	public async Task<SubcategoryUsage> GetUsageAsync(Guid categoryId, string subcategoryName, int receiptLimit, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		// Resolve this parent by identity, even when hidden by soft deletion. Names on
		// receipt items remain historical snapshots and are never rewritten here.
		string categoryName = await context.Categories
			.IgnoreQueryFilters()
			.Where(category => category.Id == categoryId)
			.Select(category => category.Name)
			.SingleAsync(cancellationToken);

		IQueryable<ReceiptItemEntity> matchingItems = context.ReceiptItems
			.IgnoreQueryFilters()
			.Where(item => item.Category == categoryName && item.Subcategory == subcategoryName);
		int receiptItemCount = await matchingItems.CountAsync(cancellationToken);
		List<SubcategoryAffectedReceipt> affectedReceipts = await context.Receipts
			.IgnoreQueryFilters()
			.Where(receipt => matchingItems.Any(item => item.ReceiptId == receipt.Id))
			.OrderByDescending(receipt => receipt.Date)
			.ThenBy(receipt => receipt.Id)
			.Take(receiptLimit)
			.Select(receipt => new SubcategoryAffectedReceipt(receipt.Id, receipt.Date, receipt.Location, receipt.DeletedAt != null))
			.ToListAsync(cancellationToken);

		return new SubcategoryUsage(receiptItemCount, affectedReceipts);
	}
}
