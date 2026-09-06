using System.Linq.Expressions;
using Application.Exceptions;
using Application.Models;
using Domain.NormalizedDescriptions;
using Infrastructure.Entities.Core;
using Infrastructure.Extensions;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
		await using IDbContextTransaction? transaction = context.Database.IsRelational()
			? await context.Database.BeginTransactionAsync(cancellationToken) : null;
		await NormalizationWriteGuard.AcquireWriteGateAsync(context, cancellationToken);
		Dictionary<Guid, NormalizedDescriptionEntity> targets = await LoadTargetsAsync(context, entities, cancellationToken);
		foreach (ItemTemplateEntity entity in entities)
		{
			entity.NormalizedDescriptionId = ValidTarget(entity, targets);
		}
		context.ItemTemplates.AddRange(entities);
		await context.SaveChangesAsync(cancellationToken);
		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken);
		}
		return entities;
	}

	public async Task<Dictionary<Guid, string>> GetUpdateRevisionsAsync(List<Guid> ids, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await TemplateWriteGuard.ReadRevisionsAsync(context, ids.Distinct().ToArray(), cancellationToken);
	}

	public async Task UpdateAsync(List<ItemTemplateEntity> entities, IReadOnlyDictionary<Guid, string> expectedRevisions,
		CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		await using IDbContextTransaction? transaction = context.Database.IsRelational()
			? await context.Database.BeginTransactionAsync(cancellationToken) : null;
		await NormalizationWriteGuard.AcquireWriteGateAsync(context, cancellationToken);
		Dictionary<Guid, NormalizedDescriptionEntity> targets = await LoadTargetsAsync(context, entities, cancellationToken);
		Guid[] ids = entities.Select(entity => entity.Id).Distinct().ToArray();
		await TemplateWriteGuard.LockTemplatesAsync(context, ids, cancellationToken);
		Dictionary<Guid, string> currentRevisions = await TemplateWriteGuard.ReadRevisionsAsync(context, ids, cancellationToken);
		List<ItemTemplateEntity> current = await context.ItemTemplates.Where(entity => ids.Contains(entity.Id)).ToListAsync(cancellationToken);

		// Validate the whole operation before touching any tracked value or adding an audit.
		if (ids.Any(id => !expectedRevisions.TryGetValue(id, out string? expected)
			|| !currentRevisions.TryGetValue(id, out string? actual)
			|| !string.Equals(expected, actual, StringComparison.Ordinal)))
		{
			throw new ConcurrencyConflictException("An item template changed while it was being updated. Reload it and try again.");
		}

		foreach (ItemTemplateEntity entity in entities)
		{
			ItemTemplateEntity stored = current.Single(item => item.Id == entity.Id);
			stored.Name = entity.Name;
			stored.DefaultCategory = entity.DefaultCategory;
			stored.DefaultSubcategory = entity.DefaultSubcategory;
			stored.DefaultUnitPrice = entity.DefaultUnitPrice;
			stored.DefaultUnitPriceCurrency = entity.DefaultUnitPriceCurrency;
			stored.DefaultItemCode = entity.DefaultItemCode;
			stored.Description = entity.Description;
			stored.NormalizedDescriptionId = ValidTarget(entity, targets);
		}

		await context.SaveChangesAsync(cancellationToken);
		if (transaction is not null)
		{
			await transaction.CommitAsync(cancellationToken);
		}
	}

	private static async Task<Dictionary<Guid, NormalizedDescriptionEntity>> LoadTargetsAsync(
		ApplicationDbContext context, List<ItemTemplateEntity> entities, CancellationToken cancellationToken)
	{
		Guid[] ids = entities.Where(entity => entity.NormalizedDescriptionId.HasValue)
			.Select(entity => entity.NormalizedDescriptionId!.Value).Distinct().ToArray();
		await NormalizationWriteGuard.LockTargetsAsync(context, ids, cancellationToken);
		return await context.NormalizedDescriptions.Where(target => ids.Contains(target.Id))
			.ToDictionaryAsync(target => target.Id, cancellationToken);
	}

	private static Guid? ValidTarget(ItemTemplateEntity entity, Dictionary<Guid, NormalizedDescriptionEntity> targets)
		=> entity.NormalizedDescriptionId is { } id && targets.TryGetValue(id, out NormalizedDescriptionEntity? target)
			&& target.Status != NormalizedDescriptionStatus.Rejected
			&& string.Equals(target.CanonicalName, entity.Name.Trim(), StringComparison.OrdinalIgnoreCase) ? id : null;

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
