using Application.Interfaces.Services;
using Application.Models.Ynab;
using Common;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;

namespace Infrastructure.Services;

public class YnabSyncRecordService(IYnabSyncRecordRepository repository) : IYnabSyncRecordService
{
	public async Task<YnabSyncRecordDto> CreateAsync(Guid localTransactionId, string ynabBudgetId, YnabSyncType syncType, CancellationToken cancellationToken)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		YnabSyncRecordEntity entity = new()
		{
			LocalTransactionId = localTransactionId,
			YnabBudgetId = ynabBudgetId,
			SyncType = syncType,
			SyncStatus = YnabSyncStatus.Pending,
			CreatedAt = now,
			UpdatedAt = now,
		};

		YnabSyncRecordEntity created = await repository.CreateAsync(entity, cancellationToken);
		return ToDto(created);
	}

	public async Task<YnabSyncRecordDto?> GetByTransactionAndTypeAsync(Guid localTransactionId, YnabSyncType syncType, CancellationToken cancellationToken)
	{
		YnabSyncRecordEntity? entity = await repository.GetByTransactionAndTypeAsync(localTransactionId, syncType, cancellationToken);
		return entity is null ? null : ToDto(entity);
	}

	public async Task UpdateStatusAsync(Guid id, YnabSyncStatus status, string? ynabTransactionId, string? lastError, CancellationToken cancellationToken)
	{
		YnabSyncRecordEntity? entity = await repository.GetByIdAsync(id, cancellationToken);
		if (entity is null)
		{
			return;
		}

		entity.SyncStatus = status;
		entity.YnabTransactionId = ynabTransactionId ?? entity.YnabTransactionId;
		entity.LastError = lastError;
		entity.UpdatedAt = DateTimeOffset.UtcNow;

		if (status == YnabSyncStatus.Synced)
		{
			entity.SyncedAtUtc = DateTimeOffset.UtcNow;
		}

		await repository.UpdateAsync(entity, cancellationToken);
	}

	private static YnabSyncRecordDto ToDto(YnabSyncRecordEntity entity) => new(
		entity.Id,
		entity.LocalTransactionId,
		entity.YnabTransactionId,
		entity.YnabBudgetId,
		entity.YnabAccountId,
		entity.SyncType,
		entity.SyncStatus,
		entity.SyncedAtUtc,
		entity.LastError,
		entity.CreatedAt,
		entity.UpdatedAt);
}
