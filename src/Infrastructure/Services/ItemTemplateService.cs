using Application.Exceptions;
using Application.Interfaces.Services;
using Application.Models;
using Domain.Core;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Services;

public class ItemTemplateService(IItemTemplateRepository repository, ItemTemplateMapper mapper) : IItemTemplateService
{
	public async Task<List<ItemTemplate>> CreateAsync(List<ItemTemplate> models, CancellationToken cancellationToken)
	{
		List<ItemTemplateEntity> entities = [.. models.Select(mapper.ToEntity)];
		List<ItemTemplateEntity> createdEntities = await repository.CreateAsync(entities, cancellationToken);
		return [.. createdEntities.Select(mapper.ToDomain)];
	}

	public async Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken)
	{
		await repository.DeleteAsync(ids, cancellationToken);
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		return await repository.ExistsAsync(id, cancellationToken);
	}

	public async Task<PagedResult<ItemTemplate>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		int total = await repository.GetCountAsync(cancellationToken);
		List<ItemTemplateEntity> entities = await repository.GetAllAsync(offset, limit, sort, cancellationToken);
		List<ItemTemplate> data = [.. entities.Select(mapper.ToDomain)];
		return new PagedResult<ItemTemplate>(data, total, offset, limit);
	}

	public async Task<PagedResult<ItemTemplate>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		int total = await repository.GetDeletedCountAsync(cancellationToken);
		List<ItemTemplateEntity> entities = await repository.GetDeletedAsync(offset, limit, sort, cancellationToken);
		List<ItemTemplate> data = [.. entities.Select(mapper.ToDomain)];
		return new PagedResult<ItemTemplate>(data, total, offset, limit);
	}

	public async Task<ItemTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		ItemTemplateEntity? entity = await repository.GetByIdAsync(id, cancellationToken);
		return entity == null ? null : mapper.ToDomain(entity);
	}

	public async Task<int> GetCountAsync(CancellationToken cancellationToken)
	{
		return await repository.GetCountAsync(cancellationToken);
	}

	public async Task UpdateAsync(List<ItemTemplate> models, CancellationToken cancellationToken)
	{
		List<ItemTemplateEntity> entities = [.. models.Select(mapper.ToEntity)];
		await repository.UpdateAsync(entities, cancellationToken);
	}

	public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
	{
		// The unique index on Name is filtered to DeletedAt IS NULL, so a name freed by
		// soft-delete can be reused by a new active template. Pre-check for that collision and
		// surface a clean 409 (via DuplicateEntityException → GlobalExceptionHandlerMiddleware)
		// instead of the raw unique-violation 500 that would otherwise block restore forever
		// (RECEIPTS-772).
		string? conflictingName = await repository.GetRestoreConflictNameAsync(id, cancellationToken);
		if (conflictingName is not null)
		{
			throw new DuplicateEntityException(
				$"Cannot restore this item template because an active template named '{conflictingName}' already exists.");
		}

		try
		{
			return await repository.RestoreAsync(id, cancellationToken);
		}
		catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
		{
			// Lost a race: an active row with the same name appeared between the pre-check and
			// SaveChanges. Still map to 409 rather than letting a 500 escape.
			throw new DuplicateEntityException("An item template with this name already exists.", ex);
		}
	}
}
