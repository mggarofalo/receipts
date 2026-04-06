using Application.Interfaces.Services;
using Application.Models.Ynab;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;

namespace Infrastructure.Services;

public class YnabAccountMappingService(IYnabAccountMappingRepository repository) : IYnabAccountMappingService
{
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

		await repository.UpdateAsync(entity, cancellationToken);
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		await repository.DeleteAsync(id, cancellationToken);
	}

	public async Task<int> CountStaleMappingsAsync(string currentBudgetId, CancellationToken cancellationToken)
	{
		return await repository.CountByBudgetIdNotAsync(currentBudgetId, cancellationToken);
	}

	public async Task<int> DeleteStaleMappingsAsync(string currentBudgetId, CancellationToken cancellationToken)
	{
		return await repository.DeleteByBudgetIdNotAsync(currentBudgetId, cancellationToken);
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
