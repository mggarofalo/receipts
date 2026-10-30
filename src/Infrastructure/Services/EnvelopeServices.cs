using Application.Envelopes;
using Application.Exceptions;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Models.Ynab;
using Domain.Envelopes;
using Infrastructure.Entities.Envelopes;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services;

public class EnvelopeService(IDbContextFactory<ApplicationDbContext> contextFactory) : IEnvelopeService
{
	public async Task<List<Envelope>> GetAllAsync(bool? isActive, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		return await db.Envelopes.AsNoTracking().Where(row => isActive == null || row.IsActive == isActive)
			.OrderBy(row => row.Name).Select(row => new Envelope(row.Id, row.Name, row.IsActive)).ToListAsync(cancellationToken);
	}

	public async Task<Envelope?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		return await db.Envelopes.AsNoTracking().Where(row => row.Id == id)
			.Select(row => new Envelope(row.Id, row.Name, row.IsActive)).SingleOrDefaultAsync(cancellationToken);
	}

	public async Task<Envelope> CreateAsync(Envelope envelope, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		EnvelopeEntity entity = new() { Id = Guid.NewGuid(), Name = envelope.Name.Trim(), IsActive = envelope.IsActive };
		if (await db.Envelopes.AnyAsync(row => row.Name == entity.Name && row.Id != entity.Id, cancellationToken))
		{
			throw new DuplicateEntityException($"An envelope named '{entity.Name}' already exists.");
		}
		db.Envelopes.Add(entity);
		await db.SaveChangesAsync(cancellationToken);
		return new(entity.Id, entity.Name, entity.IsActive);
	}

	public async Task<bool> UpdateAsync(Envelope envelope, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		EnvelopeEntity? entity = await db.Envelopes.SingleOrDefaultAsync(row => row.Id == envelope.Id, cancellationToken);
		if (entity is null)
		{
			return false;
		}
		entity.Name = envelope.Name.Trim();
		entity.IsActive = envelope.IsActive;
		if (await db.Envelopes.AnyAsync(row => row.Name == entity.Name && row.Id != entity.Id, cancellationToken))
		{
			throw new DuplicateEntityException($"An envelope named '{entity.Name}' already exists.");
		}
		await db.SaveChangesAsync(cancellationToken);
		return true;
	}

	public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		EnvelopeEntity? entity = await db.Envelopes.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
		if (entity is null)
		{
			return false;
		}
		// History is never re-pointed: an envelope purchases or defaults use is archived, not deleted.
		int items = await db.ReceiptItems.IgnoreQueryFilters().CountAsync(row => row.EnvelopeId == id, cancellationToken);
		int receipts = await db.Receipts.IgnoreQueryFilters().CountAsync(row => row.PaidFromEnvelopeId == id, cancellationToken);
		int defaults = await db.ItemTypeEnvelopeDefaults.CountAsync(row => row.EnvelopeId == id, cancellationToken);
		if (items + receipts + defaults > 0)
		{
			throw new EnvelopeException(409, $"Cannot delete — the envelope is used by {items} item(s), {receipts} receipt(s) and {defaults} item-type default(s). Archive it instead.",
				new Dictionary<string, object?> { ["itemCount"] = items, ["receiptCount"] = receipts, ["defaultCount"] = defaults });
		}
		db.YnabEnvelopeMappings.RemoveRange(db.YnabEnvelopeMappings.Where(row => row.EnvelopeId == id));
		db.Envelopes.Remove(entity);
		await db.SaveChangesAsync(cancellationToken);
		return true;
	}
}

public class OccasionService(IDbContextFactory<ApplicationDbContext> contextFactory) : IOccasionService
{
	public async Task<List<Occasion>> GetAllAsync(bool? isActive, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		return await db.Occasions.AsNoTracking().Where(row => isActive == null || row.IsActive == isActive)
			.OrderBy(row => row.Name).Select(row => new Occasion(row.Id, row.Name, row.IsActive)).ToListAsync(cancellationToken);
	}

	public async Task<Occasion?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		return await db.Occasions.AsNoTracking().Where(row => row.Id == id)
			.Select(row => new Occasion(row.Id, row.Name, row.IsActive)).SingleOrDefaultAsync(cancellationToken);
	}

	public async Task<Occasion> CreateAsync(Occasion occasion, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		OccasionEntity entity = new() { Id = Guid.NewGuid(), Name = occasion.Name.Trim(), IsActive = occasion.IsActive };
		if (await db.Occasions.AnyAsync(row => row.Name == entity.Name && row.Id != entity.Id, cancellationToken))
		{
			throw new DuplicateEntityException($"An occasion named '{entity.Name}' already exists.");
		}
		db.Occasions.Add(entity);
		await db.SaveChangesAsync(cancellationToken);
		return new(entity.Id, entity.Name, entity.IsActive);
	}

	public async Task<bool> UpdateAsync(Occasion occasion, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		OccasionEntity? entity = await db.Occasions.SingleOrDefaultAsync(row => row.Id == occasion.Id, cancellationToken);
		if (entity is null)
		{
			return false;
		}
		entity.Name = occasion.Name.Trim();
		entity.IsActive = occasion.IsActive;
		if (await db.Occasions.AnyAsync(row => row.Name == entity.Name && row.Id != entity.Id, cancellationToken))
		{
			throw new DuplicateEntityException($"An occasion named '{entity.Name}' already exists.");
		}
		await db.SaveChangesAsync(cancellationToken);
		return true;
	}

	public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		OccasionEntity? entity = await db.Occasions.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
		if (entity is null)
		{
			return false;
		}
		int items = await db.ReceiptItems.IgnoreQueryFilters().CountAsync(row => row.OccasionId == id, cancellationToken);
		int receipts = await db.Receipts.IgnoreQueryFilters().CountAsync(row => row.OccasionId == id, cancellationToken);
		if (items + receipts > 0)
		{
			throw new EnvelopeException(409, $"Cannot delete — the occasion is used by {items} item(s) and {receipts} receipt(s). Archive it instead.",
				new Dictionary<string, object?> { ["itemCount"] = items, ["receiptCount"] = receipts });
		}
		db.Occasions.Remove(entity);
		await db.SaveChangesAsync(cancellationToken);
		return true;
	}
}

public class ItemTypeEnvelopeDefaultService(IDbContextFactory<ApplicationDbContext> contextFactory, TimeProvider clock) : IItemTypeEnvelopeDefaultService
{
	public async Task<List<ItemTypeEnvelopeDefault>> GetAllAsync(CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		return await db.ItemTypeEnvelopeDefaults.AsNoTracking().OrderBy(row => row.CategoryId)
			.Select(row => new ItemTypeEnvelopeDefault(row.CategoryId, row.EnvelopeId, row.UpdatedAt)).ToListAsync(cancellationToken);
	}

	public async Task<ItemTypeEnvelopeDefault> SetAsync(Guid categoryId, Guid envelopeId, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		Guid? head = await db.TaxonomyHeads.AsNoTracking().Select(row => (Guid?)row.RevisionId).SingleOrDefaultAsync(cancellationToken);
		if (head is null || !await db.TaxonomyNodes.AnyAsync(node => node.RevisionId == head && node.CategoryId == categoryId && !node.Archived, cancellationToken))
		{
			throw new EnvelopeException(400, "Item type must be selectable in the current taxonomy.");
		}
		await ItemEnvelopeWriter.RequireChoicesAsync(db, [envelopeId], [], cancellationToken);
		DateTimeOffset now = clock.GetUtcNow();
		ItemTypeEnvelopeDefaultEntity? entity = await db.ItemTypeEnvelopeDefaults.SingleOrDefaultAsync(row => row.CategoryId == categoryId, cancellationToken);
		if (entity is null)
		{
			entity = new() { Id = Guid.NewGuid(), CategoryId = categoryId, CreatedAt = now };
			db.ItemTypeEnvelopeDefaults.Add(entity);
		}
		entity.EnvelopeId = envelopeId;
		entity.UpdatedAt = now;
		await db.SaveChangesAsync(cancellationToken);
		return new(entity.CategoryId, entity.EnvelopeId, entity.UpdatedAt);
	}

	public async Task<bool> DeleteAsync(Guid categoryId, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		ItemTypeEnvelopeDefaultEntity? entity = await db.ItemTypeEnvelopeDefaults.SingleOrDefaultAsync(row => row.CategoryId == categoryId, cancellationToken);
		if (entity is null)
		{
			return false;
		}
		db.ItemTypeEnvelopeDefaults.Remove(entity);
		await db.SaveChangesAsync(cancellationToken);
		return true;
	}
}

public class YnabEnvelopeMappingService(IDbContextFactory<ApplicationDbContext> contextFactory, TimeProvider clock,
	ICommittedChangePublisher committedChangePublisher) : IYnabEnvelopeMappingService
{
	public async Task<List<YnabEnvelopeMappingDto>> GetByBudgetIdAsync(string ynabBudgetId, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		return await (from mapping in db.YnabEnvelopeMappings.AsNoTracking()
					  join envelope in db.Envelopes.IgnoreQueryFilters().AsNoTracking() on mapping.EnvelopeId equals envelope.Id
					  where mapping.YnabBudgetId == ynabBudgetId
					  orderby envelope.Name
					  select new YnabEnvelopeMappingDto(mapping.Id, mapping.EnvelopeId, envelope.Name, mapping.YnabCategoryId,
						  mapping.YnabCategoryName, mapping.YnabCategoryGroupName, mapping.YnabBudgetId, mapping.CreatedAt, mapping.UpdatedAt))
			.ToListAsync(cancellationToken);
	}

	public async Task<YnabEnvelopeMappingDto> SetAsync(Guid envelopeId, string ynabBudgetId, string ynabCategoryId,
		string ynabCategoryName, string ynabCategoryGroupName, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		string envelopeName = await db.Envelopes.Where(row => row.Id == envelopeId).Select(row => row.Name).SingleOrDefaultAsync(cancellationToken)
			?? throw new KeyNotFoundException($"Envelope {envelopeId} not found");
		DateTimeOffset now = clock.GetUtcNow();
		YnabEnvelopeMappingEntity? entity = await db.YnabEnvelopeMappings
			.SingleOrDefaultAsync(row => row.EnvelopeId == envelopeId && row.YnabBudgetId == ynabBudgetId, cancellationToken);
		bool created = entity is null;
		if (entity is null)
		{
			entity = new() { Id = Guid.NewGuid(), EnvelopeId = envelopeId, YnabBudgetId = ynabBudgetId, CreatedAt = now };
			db.YnabEnvelopeMappings.Add(entity);
		}
		entity.YnabCategoryId = ynabCategoryId;
		entity.YnabCategoryName = ynabCategoryName;
		entity.YnabCategoryGroupName = ynabCategoryGroupName;
		entity.UpdatedAt = now;
		try
		{
			await db.SaveChangesAsync(cancellationToken);
		}
		catch (DbUpdateException) when (created)
		{
			throw new DuplicateEntityException("This envelope was linked to YNAB concurrently. Refresh and retry.");
		}
		await committedChangePublisher.PublishAsync(new CommittedEntityChange(CommittedEntityType.YnabMapping,
			created ? CommittedChangeType.Created : CommittedChangeType.Updated, entity.Id));
		return new(entity.Id, entity.EnvelopeId, envelopeName, entity.YnabCategoryId, entity.YnabCategoryName,
			entity.YnabCategoryGroupName, entity.YnabBudgetId, entity.CreatedAt, entity.UpdatedAt);
	}

	public async Task<bool> DeleteAsync(Guid envelopeId, string ynabBudgetId, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		YnabEnvelopeMappingEntity? entity = await db.YnabEnvelopeMappings
			.SingleOrDefaultAsync(row => row.EnvelopeId == envelopeId && row.YnabBudgetId == ynabBudgetId, cancellationToken);
		if (entity is null)
		{
			return false;
		}
		db.YnabEnvelopeMappings.Remove(entity);
		await db.SaveChangesAsync(cancellationToken);
		await committedChangePublisher.PublishAsync(new CommittedEntityChange(CommittedEntityType.YnabMapping,
			CommittedChangeType.Deleted, entity.Id));
		return true;
	}

	public async Task<List<Envelope>> GetUnmappedEnvelopesAsync(string ynabBudgetId, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		return await db.Envelopes.IgnoreQueryFilters().AsNoTracking()
			.Where(envelope => (envelope.DeletedAt == null && envelope.IsActive) || db.ReceiptItems.Any(item => item.EnvelopeId == envelope.Id))
			.Where(envelope => !db.YnabEnvelopeMappings.Any(mapping => mapping.EnvelopeId == envelope.Id && mapping.YnabBudgetId == ynabBudgetId))
			.OrderBy(envelope => envelope.Name)
			.Select(envelope => new Envelope(envelope.Id, envelope.Name, envelope.IsActive)).ToListAsync(cancellationToken);
	}

	public async Task<int> CountStaleMappingsAsync(string currentBudgetId, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		return await db.YnabEnvelopeMappings.CountAsync(row => row.YnabBudgetId != currentBudgetId, cancellationToken);
	}

	public async Task<int> DeleteStaleMappingsAsync(string currentBudgetId, CancellationToken cancellationToken)
	{
		await using ApplicationDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
		List<YnabEnvelopeMappingEntity> stale = await db.YnabEnvelopeMappings.Where(row => row.YnabBudgetId != currentBudgetId).ToListAsync(cancellationToken);
		db.YnabEnvelopeMappings.RemoveRange(stale);
		await db.SaveChangesAsync(cancellationToken);
		if (stale.Count > 0)
		{
			await committedChangePublisher.PublishAsync(new CommittedEntityChange(CommittedEntityType.YnabMapping, CommittedChangeType.Deleted));
		}
		return stale.Count;
	}
}
