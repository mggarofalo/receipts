using Application.Interfaces.Services;
using Application.Models;
using Domain.Core;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;

namespace Infrastructure.Services;

public class ReceiptService(IReceiptRepository repository, ReceiptMapper mapper) : IReceiptService
{
	public async Task<List<Receipt>> CreateAsync(List<Receipt> models, CancellationToken cancellationToken)
	{
		List<ReceiptEntity> receiptEntities = [.. models.Select(mapper.ToEntity)];
		List<ReceiptEntity> createdReceiptEntities = await repository.CreateAsync(receiptEntities, cancellationToken);
		return [.. createdReceiptEntities.Select(mapper.ToDomain)];
	}

	public async Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken)
	{
		await repository.DeleteAsync(ids, cancellationToken);
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		return await repository.ExistsAsync(id, cancellationToken);
	}

	public async Task<PagedResult<Receipt>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		return await GetAllAsync(offset, limit, sort, accountId: null, cardId: null, q: null, location: null, cancellationToken);
	}

	public async Task<PagedResult<Receipt>> GetAllAsync(int offset, int limit, SortParams sort, Guid? accountId, Guid? cardId, string? q, string? location, CancellationToken cancellationToken)
	{
		int total = await repository.GetCountAsync(accountId, cardId, q, location, cancellationToken);
		List<ReceiptEntity> entities = await repository.GetAllAsync(offset, limit, sort, accountId, cardId, q, location, cancellationToken);
		List<Receipt> data = [.. entities.Select(mapper.ToDomain)];
		return new PagedResult<Receipt>(data, total, offset, limit);
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

	public async Task UpdateImagePathsAsync(Guid receiptId, string originalImagePath, string processedImagePath, CancellationToken cancellationToken)
	{
		await repository.UpdateImagePathsAsync(receiptId, originalImagePath, processedImagePath, cancellationToken);
	}

	public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
	{
		return await repository.RestoreAsync(id, cancellationToken);
	}

	public async Task<List<string>> GetDistinctLocationsAsync(string? query, int limit, CancellationToken cancellationToken)
	{
		return await repository.GetDistinctLocationsAsync(query, limit, cancellationToken);
	}
}
