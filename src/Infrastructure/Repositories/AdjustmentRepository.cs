using System.Linq.Expressions;
using Application.Models;
using Infrastructure.Entities.Core;
using Infrastructure.Extensions;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class AdjustmentRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : IAdjustmentRepository
{
	private static readonly Dictionary<string, Expression<Func<AdjustmentEntity, object>>> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
	{
		["type"] = e => e.Type,
		["amount"] = e => e.Amount,
		["description"] = e => e.Description!,
	};

	public async Task<AdjustmentEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Adjustments.FindAsync([id], cancellationToken);
	}

	public async Task<List<AdjustmentEntity>> GetByReceiptIdAsync(Guid receiptId, int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Adjustments
			.IgnoreAutoIncludes()
			.Where(a => a.ReceiptId == receiptId)
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Type, e => e.Id)
			.Skip(offset)
			.Take(limit)
			.Select(a => new AdjustmentEntity
			{
				Id = a.Id,
				ReceiptId = a.ReceiptId,
				Type = a.Type,
				Amount = a.Amount,
				AmountCurrency = a.AmountCurrency,
				Description = a.Description
			})
			.ToListAsync(cancellationToken);
	}

	public async Task<int> GetByReceiptIdCountAsync(Guid receiptId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Adjustments
			.Where(a => a.ReceiptId == receiptId)
			.CountAsync(cancellationToken);
	}

	public async Task<List<AdjustmentEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Adjustments
			.IgnoreAutoIncludes()
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Type, e => e.Id)
			.Skip(offset)
			.Take(limit)
			.Select(a => new AdjustmentEntity
			{
				Id = a.Id,
				ReceiptId = a.ReceiptId,
				Type = a.Type,
				Amount = a.Amount,
				AmountCurrency = a.AmountCurrency,
				Description = a.Description
			})
			.ToListAsync(cancellationToken);
	}

	public async Task<List<AdjustmentEntity>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Adjustments
			.OnlyDeleted()
			.Where(a => a.CascadeDeletedByParentId == null)
			.IgnoreAutoIncludes()
			.AsNoTracking()
			.ApplySort(sort, AllowedSortColumns, e => e.Type, e => e.Id)
			.Select(a => new AdjustmentEntity
			{
				Id = a.Id,
				ReceiptId = a.ReceiptId,
				Type = a.Type,
				Amount = a.Amount,
				AmountCurrency = a.AmountCurrency,
				Description = a.Description,
				DeletedAt = a.DeletedAt
			})
			.Skip(offset)
			.Take(limit)
			.ToListAsync(cancellationToken);
	}

	public async Task<int> GetDeletedCountAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Adjustments
			.OnlyDeleted()
			.Where(a => a.CascadeDeletedByParentId == null)
			.CountAsync(cancellationToken);
	}

	public async Task<List<AdjustmentEntity>> CreateAsync(List<AdjustmentEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		context.Adjustments.AddRange(entities);
		await context.SaveChangesAsync(cancellationToken);
		return entities;
	}

	public async Task UpdateAsync(List<AdjustmentEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IEnumerable<Guid> ids = entities.Select(e => e.Id);
		List<AdjustmentEntity> existingEntities = await context.Adjustments
			.IgnoreAutoIncludes()
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		foreach (AdjustmentEntity entity in entities)
		{
			AdjustmentEntity existingEntity = existingEntities.Single(e => e.Id == entity.Id);
			context.Entry(existingEntity).CurrentValues.SetValues(entity);
		}

		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		List<AdjustmentEntity> entities = await context.Adjustments
			.IgnoreAutoIncludes()
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		context.Adjustments.RemoveRange(entities);
		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Adjustments.AnyAsync(e => e.Id == id, cancellationToken);
	}

	public async Task<int> GetCountAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Adjustments.CountAsync(cancellationToken);
	}

	public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		AdjustmentEntity? entity = await context.Adjustments
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
}
