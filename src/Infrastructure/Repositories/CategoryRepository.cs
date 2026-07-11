using System.Linq.Expressions;
using Application.Models;
using Infrastructure.Entities.Core;
using Infrastructure.Extensions;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class CategoryRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : ICategoryRepository
{
	private static readonly Dictionary<string, Expression<Func<CategoryEntity, object>>> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
	{
		["name"] = e => e.Name,
	};

	public async Task<CategoryEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Categories.FindAsync([id], cancellationToken);
	}

	public async Task<List<CategoryEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken, bool? isActive = null)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<CategoryEntity> query = context.Categories.AsNoTracking();
		if (isActive.HasValue)
		{
			query = query.Where(e => e.IsActive == isActive.Value);
		}

		return await query
			.ApplySort(sort, AllowedSortColumns, e => e.Name, e => e.Id)
			.Skip(offset)
			.Take(limit)
			.ToListAsync(cancellationToken);
	}

	public async Task<List<CategoryEntity>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Categories
			.OnlyDeleted()
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Name, e => e.Id)
			.Select(c => new CategoryEntity
			{
				Id = c.Id,
				Name = c.Name,
				Description = c.Description,
				IsActive = c.IsActive,
				DeletedAt = c.DeletedAt
			})
			.Skip(offset)
			.Take(limit)
			.ToListAsync(cancellationToken);
	}

	public async Task<int> GetDeletedCountAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Categories
			.OnlyDeleted()
			.CountAsync(cancellationToken);
	}

	public async Task<List<CategoryEntity>> CreateAsync(List<CategoryEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		context.Categories.AddRange(entities);
		await context.SaveChangesAsync(cancellationToken);
		return entities;
	}

	public async Task UpdateAsync(List<CategoryEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IEnumerable<Guid> ids = entities.Select(e => e.Id);
		List<CategoryEntity> existingEntities = await context.Categories
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		foreach (CategoryEntity entity in entities)
		{
			CategoryEntity existingEntity = existingEntities.Single(e => e.Id == entity.Id);
			context.Entry(existingEntity).CurrentValues.SetValues(entity);
		}

		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Categories.AnyAsync(e => e.Id == id, cancellationToken);
	}

	public async Task<int> GetCountAsync(CancellationToken cancellationToken, bool? isActive = null)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<CategoryEntity> query = context.Categories;
		if (isActive.HasValue)
		{
			query = query.Where(e => e.IsActive == isActive.Value);
		}

		return await query.CountAsync(cancellationToken);
	}

	public async Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		List<CategoryEntity> entities = await context.Categories
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		// Load owned children into the change tracker so cascade soft-delete fires
		await context.Subcategories.IgnoreAutoIncludes().Where(s => ids.Contains(s.CategoryId)).LoadAsync(cancellationToken);

		context.Categories.RemoveRange(entities);
		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		CategoryEntity? entity = await context.Categories
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

		await context.RestoreOwnedChildrenAsync<CategoryEntity>(id, cancellationToken);

		await context.SaveChangesAsync(cancellationToken);
		return true;
	}

	public async Task<string?> GetRestoreConflictNameAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();

		// The soft-deleted row the caller intends to restore.
		CategoryEntity? deleted = await context.Categories
			.IncludeDeleted()
			.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt != null, cancellationToken);

		if (deleted is null)
		{
			// Nothing to restore (absent or already active) — no conflict to report.
			return null;
		}

		// context.Categories is filtered to active rows (DeletedAt == null). A match means an
		// active category already owns this name, so restoring would violate the filtered
		// unique index on Name. Return the colliding name so the caller can surface a 409.
		bool conflict = await context.Categories
			.AnyAsync(e => e.Name == deleted.Name, cancellationToken);

		return conflict ? deleted.Name : null;
	}

	public async Task<int> GetSubcategoryCountAsync(Guid categoryId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Subcategories
			.CountAsync(s => s.CategoryId == categoryId, cancellationToken);
	}

	public async Task<int> GetReceiptItemCountByCategoryNameAsync(string categoryName, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.ReceiptItems
			.IgnoreQueryFilters()
			.CountAsync(ri => ri.Category == categoryName, cancellationToken);
	}

	public async Task<List<string>> GetSubcategoryNamesAsync(Guid categoryId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Subcategories
			.Where(s => s.CategoryId == categoryId)
			.Select(s => s.Name)
			.ToListAsync(cancellationToken);
	}

	public async Task<int> GetReceiptItemCountBySubcategoryNamesAsync(List<string> subcategoryNames, CancellationToken cancellationToken)
	{
		if (subcategoryNames.Count == 0)
		{
			return 0;
		}

		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.ReceiptItems
			.IgnoreQueryFilters()
			.CountAsync(ri => ri.Subcategory != null && subcategoryNames.Contains(ri.Subcategory), cancellationToken);
	}
}
