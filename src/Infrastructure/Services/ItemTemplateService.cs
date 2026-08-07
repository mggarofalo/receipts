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

public class ItemTemplateService(
	IItemTemplateRepository repository,
	ItemTemplateMapper mapper,
	INormalizedDescriptionService normalizedDescriptions) : IItemTemplateService
{
	/// <summary>
	/// Gives each model the canonical entry its name declares (RECEIPTS-881).
	/// </summary>
	/// <remarks>
	/// Creating a template is a user saying "this item exists and is called X", which is exactly
	/// what a canonical entry records — so the entry is created up front and <c>Active</c>, and
	/// every item later entered from the template is stamped with it and skips the resolver.
	///
	/// Failure here does not fail the template. The canonical entry is a convenience link, and the
	/// embedding service can be unconfigured or transiently down; refusing to save someone's
	/// template because the classifier is unavailable trades a working feature for a bookkeeping
	/// one. An unlinked template links on its next use.
	/// </remarks>
	private async Task LinkCanonicalEntriesAsync(List<ItemTemplate> models, CancellationToken cancellationToken)
	{
		foreach (ItemTemplate model in models)
		{
			try
			{
				Domain.NormalizedDescriptions.NormalizedDescription canonical =
					await normalizedDescriptions.GetOrCreateForTemplateAsync(model.Name, cancellationToken);
				model.NormalizedDescriptionId = canonical.Id;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// Left unlinked deliberately — see the remarks above. Swallowing this is only
				// acceptable because the link is recoverable on next use; if it ever becomes
				// load-bearing, this must become a hard failure.
				model.NormalizedDescriptionId = null;
			}
		}
	}

	public async Task<List<ItemTemplate>> CreateAsync(List<ItemTemplate> models, CancellationToken cancellationToken)
	{
		await LinkCanonicalEntriesAsync(models, cancellationToken);
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
		// Re-resolved on every update rather than only when the name changed. The name is what the
		// canonical entry is keyed on, and comparing against the stored name would need a read per
		// template to find out whether it moved — GetOrCreateForTemplateAsync already answers
		// "which entry is this name?" with a single indexed lookup, and returns the existing row
		// unchanged when nothing moved. Renaming a template therefore re-points it at the entry
		// for its new name; the old entry is left alone, since other receipt items may still be
		// grouped under it and deleting it would silently move their spending.
		await LinkCanonicalEntriesAsync(models, cancellationToken);
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
