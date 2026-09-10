using Application.Interfaces.Services;
using Application.Models;
using Application.Models.CommittedChanges;
using Application.Models.Images;
using Domain.Core;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;

namespace Infrastructure.Services;

public class ReceiptService(
	IReceiptRepository repository,
	ReceiptMapper mapper,
	ICommittedChangePublisher? committedChangePublisher = null) : IReceiptService
{
	private readonly ICommittedChangePublisher _committedChangePublisher =
		committedChangePublisher ?? new NullCommittedChangePublisher();

	public async Task<List<Receipt>> CreateAsync(List<Receipt> models, CancellationToken cancellationToken)
	{
		List<ReceiptEntity> receiptEntities = [.. models.Select(mapper.ToEntity)];
		List<ReceiptEntity> createdReceiptEntities = await repository.CreateAsync(receiptEntities, cancellationToken);
		return [.. createdReceiptEntities.Select(mapper.ToDomain)];
	}

	public async Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken)
	{
		CascadeMutationResult result = await repository.DeleteAsync(ids, cancellationToken);
		if (result.YnabSyncRecordsChanged > 0)
		{
			await PublishYnabSyncRecordCollectionChangeAsync(CommittedChangeType.Deleted);
		}
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		return await repository.ExistsAsync(id, cancellationToken);
	}

	public async Task<PagedResult<Receipt>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		int total = await repository.GetCountAsync(cancellationToken);
		List<ReceiptEntity> entities = await repository.GetAllAsync(offset, limit, sort, cancellationToken);
		return new PagedResult<Receipt>([.. entities.Select(mapper.ToDomain)], total, offset, limit);
	}

	public async Task<PagedResult<Receipt>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		int total = await repository.GetDeletedCountAsync(cancellationToken);
		List<ReceiptEntity> entities = await repository.GetDeletedAsync(offset, limit, sort, cancellationToken);
		List<Receipt> data = [.. entities.Select(mapper.ToDomain)];
		return new PagedResult<Receipt>(data, total, offset, limit);
	}

	public async Task<Receipt?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		ReceiptEntity? receiptEntity = await repository.GetByIdAsync(id, cancellationToken);
		return receiptEntity == null ? null : mapper.ToDomain(receiptEntity);
	}

	public async Task<int> GetCountAsync(CancellationToken cancellationToken)
	{
		return await repository.GetCountAsync(cancellationToken);
	}

	public async Task UpdateAsync(List<Receipt> models, CancellationToken cancellationToken)
	{
		List<ReceiptEntity> receiptEntities = [.. models.Select(mapper.ToEntity)];
		await repository.UpdateAsync(receiptEntities, cancellationToken);
	}

	public async Task<ReceiptImageSet?> ReplaceImagePathsAsync(
		Guid receiptId,
		ReceiptImageSet imageSet,
		CancellationToken cancellationToken)
	{
		return await repository.ReplaceImagePathsAsync(receiptId, imageSet, cancellationToken);
	}

	public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
	{
		CascadeMutationResult result = await repository.RestoreAsync(id, cancellationToken);
		if (result.YnabSyncRecordsChanged > 0)
		{
			await PublishYnabSyncRecordCollectionChangeAsync(CommittedChangeType.Updated);
		}
		return result.EntityChanged;
	}

	public async Task<List<string>> GetDistinctLocationsAsync(string? query, int limit, CancellationToken cancellationToken)
	{
		return await repository.GetDistinctLocationsAsync(query, limit, cancellationToken);
	}

	private Task PublishYnabSyncRecordCollectionChangeAsync(CommittedChangeType changeType)
		=> _committedChangePublisher.PublishAsync(new CommittedEntityChange(
			CommittedEntityType.YnabSyncRecord,
			changeType,
			SuppressToast: true));
}
