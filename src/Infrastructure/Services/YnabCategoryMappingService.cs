using Application.Exceptions;
using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Models.Ynab;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Services;

public class YnabCategoryMappingService(
	IYnabCategoryMappingRepository repository,
	ICommittedChangePublisher committedChangePublisher) : IYnabCategoryMappingService
{
	public YnabCategoryMappingService(IYnabCategoryMappingRepository repository)
		: this(repository, new NullCommittedChangePublisher())
	{
	}

	public async Task<List<YnabCategoryMappingDto>> GetAllAsync(CancellationToken cancellationToken)
	{
		List<YnabCategoryMappingEntity> entities = await repository.GetAllAsync(cancellationToken);
		return entities.Select(ToDto).ToList();
	}

	public async Task<YnabCategoryMappingDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		YnabCategoryMappingEntity? entity = await repository.GetByIdAsync(id, cancellationToken);
		return entity is null ? null : ToDto(entity);
	}

	public async Task<YnabCategoryMappingDto?> GetByReceiptsCategoryAsync(string receiptsCategory, CancellationToken cancellationToken)
	{
		YnabCategoryMappingEntity? entity = await repository.GetByReceiptsCategoryAsync(receiptsCategory, cancellationToken);
		return entity is null ? null : ToDto(entity);
	}

	public async Task<YnabCategoryMappingDto> CreateAsync(
		string receiptsCategory,
		string ynabCategoryId,
		string ynabCategoryName,
		string ynabCategoryGroupName,
		string ynabBudgetId,
		CancellationToken cancellationToken)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		YnabCategoryMappingEntity entity = new()
		{
			ReceiptsCategory = receiptsCategory,
			YnabCategoryId = ynabCategoryId,
			YnabCategoryName = ynabCategoryName,
			YnabCategoryGroupName = ynabCategoryGroupName,
			YnabBudgetId = ynabBudgetId,
			CreatedAt = now,
			UpdatedAt = now,
		};

		try
		{
			YnabCategoryMappingEntity created = await repository.CreateAsync(entity, cancellationToken);
			await committedChangePublisher.PublishAsync(new CommittedEntityChange(
				CommittedEntityType.YnabMapping,
				CommittedChangeType.Created,
				created.Id));
			return ToDto(created);
		}
		catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
		{
			throw new DuplicateEntityException($"A mapping for receipts category '{receiptsCategory}' already exists.", ex);
		}
	}

	public async Task UpdateAsync(
		Guid id,
		string ynabCategoryId,
		string ynabCategoryName,
		string ynabCategoryGroupName,
		string ynabBudgetId,
		CancellationToken cancellationToken)
	{
		YnabCategoryMappingEntity? entity = await repository.GetByIdAsync(id, cancellationToken);
		if (entity is null)
		{
			return;
		}

		entity.YnabCategoryId = ynabCategoryId;
		entity.YnabCategoryName = ynabCategoryName;
		entity.YnabCategoryGroupName = ynabCategoryGroupName;
		entity.YnabBudgetId = ynabBudgetId;
		entity.UpdatedAt = DateTimeOffset.UtcNow;

		if (await repository.UpdateAsync(entity, cancellationToken))
		{
			await committedChangePublisher.PublishAsync(new CommittedEntityChange(
				CommittedEntityType.YnabMapping,
				CommittedChangeType.Updated,
				id));
		}
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		if (await repository.DeleteAsync(id, cancellationToken))
		{
			await committedChangePublisher.PublishAsync(new CommittedEntityChange(
				CommittedEntityType.YnabMapping,
				CommittedChangeType.Deleted,
				id));
		}
	}

	public async Task<int> CountStaleMappingsAsync(string currentBudgetId, CancellationToken cancellationToken)
	{
		return await repository.CountByBudgetIdNotAsync(currentBudgetId, cancellationToken);
	}

	public async Task<int> DeleteStaleMappingsAsync(string currentBudgetId, CancellationToken cancellationToken)
	{
		int deleted = await repository.DeleteByBudgetIdNotAsync(currentBudgetId, cancellationToken);
		if (deleted > 0)
		{
			await committedChangePublisher.PublishAsync(new CommittedEntityChange(
				CommittedEntityType.YnabMapping,
				CommittedChangeType.Deleted));
		}

		return deleted;
	}

	public async Task<List<string>> GetDistinctReceiptItemCategoriesAsync(CancellationToken cancellationToken)
	{
		return await repository.GetDistinctReceiptItemCategoriesAsync(cancellationToken);
	}

	public async Task<List<string>> GetUnmappedCategoriesAsync(CancellationToken cancellationToken)
	{
		List<string> allCategories = await repository.GetDistinctReceiptItemCategoriesAsync(cancellationToken);
		List<YnabCategoryMappingEntity> mappings = await repository.GetAllAsync(cancellationToken);

		HashSet<string> mappedCategories = new(mappings.Select(m => m.ReceiptsCategory), StringComparer.Ordinal);

		return allCategories
			.Where(c => !mappedCategories.Contains(c))
			.ToList();
	}

	private static YnabCategoryMappingDto ToDto(YnabCategoryMappingEntity entity) => new(
		entity.Id,
		entity.ReceiptsCategory,
		entity.YnabCategoryId,
		entity.YnabCategoryName,
		entity.YnabCategoryGroupName,
		entity.YnabBudgetId,
		entity.CreatedAt,
		entity.UpdatedAt);
}
