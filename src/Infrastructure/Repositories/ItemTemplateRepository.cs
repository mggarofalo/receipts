using System.Linq.Expressions;
using Application.Models;
using Infrastructure.Entities.Core;
using Infrastructure.Extensions;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class ItemTemplateRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : IItemTemplateRepository
{
	private static readonly Dictionary<string, Expression<Func<ItemTemplateEntity, object>>> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
	{
		["name"] = e => e.Name,
	};

	public async Task<ItemTemplateEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.ItemTemplates.FindAsync([id], cancellationToken);
	}

	public async Task<List<ItemTemplateEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken) =>
		await SearchAsync(null, offset, limit, sort, cancellationToken);

	/// <summary>
	/// One page of templates whose name contains <paramref name="q"/> (RECEIPTS-930).
	/// </summary>
	/// <remarks>
	/// Added so a picker can search the whole table instead of filtering whatever page it happened
	/// to load — the failure RECEIPTS-878 removed from the merge dialog, where a name past the cap
	/// reads as "no such thing exists".
	///
	/// A null or blank term is the unfiltered list, which is why <see cref="GetAllAsync"/> is just
	/// this with no term rather than a second query to keep in step.
	/// </remarks>
	public async Task<List<ItemTemplateEntity>> SearchAsync(string? q, int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await ApplyNameFilter(context.ItemTemplates.AsNoTracking(), q)
			.ApplySort(sort, AllowedSortColumns, e => e.Name, e => e.Id)
			.Skip(offset)
			.Take(limit)
			.ToListAsync(cancellationToken);
	}

	// Counted before paging, so a caller can page through the filtered set rather than being told
	// how many templates exist in total and then handed a shorter list.
	public async Task<int> GetCountAsync(string? q, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await ApplyNameFilter(context.ItemTemplates.AsNoTracking(), q).CountAsync(cancellationToken);
	}

	// ToLower() rather than EF.Functions.Like: it translates on both PostgreSQL and the InMemory
	// provider the unit tests use, matching how NormalizedDescriptionService searches.
	private static IQueryable<ItemTemplateEntity> ApplyNameFilter(IQueryable<ItemTemplateEntity> query, string? q)
	{
		string? trimmed = q?.Trim();
		if (string.IsNullOrEmpty(trimmed))
		{
			return query;
		}

		string lowered = trimmed.ToLowerInvariant();
		return query.Where(e => e.Name.ToLower().Contains(lowered));
	}

	public async Task<List<ItemTemplateEntity>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.ItemTemplates
			.OnlyDeleted()
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Name, e => e.Id)
			.Skip(offset)
			.Take(limit)
			.ToListAsync(cancellationToken);
	}

	public async Task<int> GetDeletedCountAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.ItemTemplates
			.OnlyDeleted()
			.CountAsync(cancellationToken);
	}

	public async Task<List<ItemTemplateEntity>> CreateAsync(List<ItemTemplateEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		context.ItemTemplates.AddRange(entities);
		await context.SaveChangesAsync(cancellationToken);
		return entities;
	}

	public async Task UpdateAsync(List<ItemTemplateEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IEnumerable<Guid> ids = entities.Select(e => e.Id);
		List<ItemTemplateEntity> existingEntities = await context.ItemTemplates
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		foreach (ItemTemplateEntity entity in entities)
		{
			ItemTemplateEntity existingEntity = existingEntities.Single(e => e.Id == entity.Id);
			context.Entry(existingEntity).CurrentValues.SetValues(entity);
		}

		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		List<ItemTemplateEntity> entities = await context.ItemTemplates
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		context.ItemTemplates.RemoveRange(entities);
		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.ItemTemplates.AnyAsync(e => e.Id == id, cancellationToken);
	}

	public async Task<int> GetCountAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.ItemTemplates.CountAsync(cancellationToken);
	}

	public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		ItemTemplateEntity? entity = await context.ItemTemplates
			.IncludeDeleted()
			.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt != null, cancellationToken);

		if (entity is null)
		{
			return false;
		}

		entity.DeletedAt = null;
		entity.DeletedByUserId = null;
		entity.DeletedByApiKeyId = null;
		await context.SaveChangesAsync(cancellationToken);
		return true;
	}

	public async Task<string?> GetRestoreConflictNameAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();

		// The soft-deleted row the caller intends to restore.
		ItemTemplateEntity? deleted = await context.ItemTemplates
			.IncludeDeleted()
			.FirstOrDefaultAsync(e => e.Id == id && e.DeletedAt != null, cancellationToken);

		if (deleted is null)
		{
			// Nothing to restore (absent or already active) — no conflict to report.
			return null;
		}

		// context.ItemTemplates is filtered to active rows (DeletedAt == null). A match means an
		// active template already owns this name, so restoring would violate the filtered unique
		// index on Name. Return the colliding name so the caller can surface a 409.
		bool conflict = await context.ItemTemplates
			.AnyAsync(e => e.Name == deleted.Name, cancellationToken);

		return conflict ? deleted.Name : null;
	}
}
