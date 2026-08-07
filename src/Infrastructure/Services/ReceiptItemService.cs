using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.ReceiptItem.GetReceiptItemSuggestions;
using Domain.Core;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;

namespace Infrastructure.Services;

public class ReceiptItemService(IReceiptItemRepository repository, ReceiptItemMapper mapper) : IReceiptItemService
{
	public async Task<List<ReceiptItem>> CreateAsync(List<ReceiptItem> models, Guid receiptId, CancellationToken cancellationToken)
	{
		List<ReceiptItemEntity> receiptItemEntities = [.. models.Select(mapper.ToEntity)];

		foreach (ReceiptItemEntity entity in receiptItemEntities)
		{
			entity.ReceiptId = receiptId;
		}

		List<ReceiptItemEntity> createdReceiptItemEntities = await repository.CreateAsync(receiptItemEntities, cancellationToken);
		return [.. createdReceiptItemEntities.Select(mapper.ToDomain)];
	}

	public async Task DeleteAsync(List<Guid> ids, CancellationToken cancellationToken)
	{
		await repository.DeleteAsync(ids, cancellationToken);
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		return await repository.ExistsAsync(id, cancellationToken);
	}

	public async Task<PagedResult<ReceiptItem>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		return await GetAllAsync(offset, limit, sort, q: null, normalizedDescriptionId: null, cancellationToken);
	}

	public async Task<PagedResult<ReceiptItem>> GetAllAsync(int offset, int limit, SortParams sort, string? q, CancellationToken cancellationToken)
	{
		return await GetAllAsync(offset, limit, sort, q, normalizedDescriptionId: null, cancellationToken);
	}

	public async Task<PagedResult<ReceiptItem>> GetAllAsync(
		int offset,
		int limit,
		SortParams sort,
		string? q,
		Guid? normalizedDescriptionId,
		CancellationToken cancellationToken)
	{
		int total = await repository.GetCountAsync(q, normalizedDescriptionId, cancellationToken);
		List<ReceiptItemEntity> entities = await repository.GetAllAsync(offset, limit, sort, q, normalizedDescriptionId, cancellationToken);
		List<ReceiptItem> data = [.. entities.Select(mapper.ToDomain)];
		return new PagedResult<ReceiptItem>(data, total, offset, limit);
	}

	public async Task<PagedResult<ReceiptItem>> GetDeletedAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		int total = await repository.GetDeletedCountAsync(cancellationToken);
		List<ReceiptItemEntity> entities = await repository.GetDeletedAsync(offset, limit, sort, cancellationToken);
		List<ReceiptItem> data = [.. entities.Select(mapper.ToDomain)];
		return new PagedResult<ReceiptItem>(data, total, offset, limit);
	}

	public async Task<ReceiptItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		ReceiptItemEntity? receiptItemEntity = await repository.GetByIdAsync(id, cancellationToken);
		return receiptItemEntity == null ? null : mapper.ToDomain(receiptItemEntity);
	}

	public async Task<PagedResult<ReceiptItem>> GetByReceiptIdAsync(Guid receiptId, int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		int total = await repository.GetByReceiptIdCountAsync(receiptId, cancellationToken);
		List<ReceiptItemEntity> entities = await repository.GetByReceiptIdAsync(receiptId, offset, limit, sort, cancellationToken);
		List<ReceiptItem> data = entities.Select(mapper.ToDomain).ToList();
		return new PagedResult<ReceiptItem>(data, total, offset, limit);
	}

	public async Task<int> GetCountAsync(CancellationToken cancellationToken)
	{
		return await repository.GetCountAsync(cancellationToken);
	}

	public async Task UpdateAsync(List<ReceiptItem> models, Guid receiptId, CancellationToken cancellationToken)
	{
		List<ReceiptItemEntity> receiptItemEntities = [.. models.Select(mapper.ToEntity)];

		foreach (ReceiptItemEntity entity in receiptItemEntities)
		{
			entity.ReceiptId = receiptId;
		}

		await repository.UpdateAsync(receiptItemEntities, cancellationToken);
	}

	public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
	{
		return await repository.RestoreAsync(id, cancellationToken);
	}

	public async Task<List<ReceiptItemSuggestion>> GetSuggestionsAsync(string itemCode, string? location, int limit, CancellationToken cancellationToken)
	{
		return await repository.GetSuggestionsAsync(itemCode, location, limit, cancellationToken);
	}
}
