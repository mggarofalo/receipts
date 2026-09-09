using Application.Interfaces.Services;
using Application.Models.CommittedChanges;
using Application.Models.Ynab;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;

namespace Infrastructure.Services;

public class YnabAccountMappingService(
	IYnabAccountMappingRepository repository,
	ICommittedChangePublisher committedChangePublisher) : IYnabAccountMappingService
{
	public YnabAccountMappingService(IYnabAccountMappingRepository repository)
		: this(repository, new NullCommittedChangePublisher())
	{
	}

	public async Task<List<YnabAccountMappingDto>> GetAllAsync(CancellationToken cancellationToken)
	{
		List<YnabAccountMappingEntity> entities = await repository.GetAllAsync(cancellationToken);
		return entities.Select(ToDto).ToList();
	}

	public async Task<YnabAccountMappingDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		YnabAccountMappingEntity? entity = await repository.GetByIdAsync(id, cancellationToken);
		return entity is null ? null : ToDto(entity);
	}

	public async Task<YnabAccountMappingDto> CreateAsync(Guid receiptsAccountId, string ynabAccountId, string ynabAccountName, string ynabBudgetId, CancellationToken cancellationToken)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		YnabAccountMappingEntity entity = new()
		{
			ReceiptsAccountId = receiptsAccountId,
			YnabAccountId = ynabAccountId,
			YnabAccountName = ynabAccountName,
			YnabBudgetId = ynabBudgetId,
			CreatedAt = now,
			UpdatedAt = now,
		};

		YnabAccountMappingEntity created = await repository.CreateAsync(entity, cancellationToken);
		await committedChangePublisher.PublishAsync(new CommittedEntityChange(
			CommittedEntityType.YnabMapping,
			CommittedChangeType.Created,
			created.Id));
		return ToDto(created);
	}

	public async Task UpdateAsync(Guid id, string ynabAccountId, string ynabAccountName, string ynabBudgetId, CancellationToken cancellationToken)
	{
		YnabAccountMappingEntity? entity = await repository.GetByIdAsync(id, cancellationToken);
		if (entity is null)
		{
			return;
		}

		entity.YnabAccountId = ynabAccountId;
		entity.YnabAccountName = ynabAccountName;
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

	private static YnabAccountMappingDto ToDto(YnabAccountMappingEntity entity) => new(
		entity.Id,
		entity.ReceiptsAccountId,
		entity.YnabAccountId,
		entity.YnabAccountName,
		entity.YnabBudgetId,
		entity.CreatedAt,
		entity.UpdatedAt);
}
