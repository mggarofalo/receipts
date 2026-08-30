using Application.Interfaces.Services;
using Application.Models;
using Domain.Core;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;

namespace Infrastructure.Services;

public class CardService(ICardRepository repository, CardMapper mapper) : ICardService
{
	public async Task<List<Card>> CreateAsync(List<Card> models, CancellationToken cancellationToken)
	{
		List<CardEntity> cardEntities = [.. models.Select(mapper.ToEntity)];
		List<CardEntity> createdCardEntities = await repository.CreateAsync(cardEntities, cancellationToken);
		return [.. createdCardEntities.Select(mapper.ToDomain)];
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		return await repository.ExistsAsync(id, cancellationToken);
	}

	public async Task<PagedResult<Card>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		int total = await repository.GetCountAsync(cancellationToken);
		List<CardEntity> entities = await repository.GetAllAsync(offset, limit, sort, cancellationToken);
		List<Card> data = [.. entities.Select(mapper.ToDomain)];
		return new PagedResult<Card>(data, total, offset, limit);
	}

	public async Task<PagedResult<Card>> GetAllAsync(int offset, int limit, SortParams sort, bool? isActive, string? q, CancellationToken cancellationToken)
	{
		int total = await repository.GetCountAsync(cancellationToken, isActive, q);
		List<CardEntity> entities = await repository.GetAllAsync(offset, limit, sort, cancellationToken, isActive, q);
		List<Card> data = [.. entities.Select(mapper.ToDomain)];
		return new PagedResult<Card>(data, total, offset, limit);
	}

	public async Task<Card?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		CardEntity? cardEntity = await repository.GetByIdAsync(id, cancellationToken);
		return cardEntity == null ? null : mapper.ToDomain(cardEntity);
	}

	public async Task<Card?> GetByTransactionIdAsync(Guid transactionId, CancellationToken cancellationToken)
	{
		CardEntity? cardEntity = await repository.GetByTransactionIdAsync(transactionId, cancellationToken);
		return cardEntity == null ? null : mapper.ToDomain(cardEntity);
	}

	public async Task<List<Card>> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken)
	{
		List<CardEntity> entities = await repository.GetByAccountIdAsync(accountId, cancellationToken);
		return [.. entities.Select(mapper.ToDomain)];
	}

	public async Task<int> GetCountAsync(CancellationToken cancellationToken)
	{
		return await repository.GetCountAsync(cancellationToken);
	}

	public async Task UpdateAsync(List<Card> models, CancellationToken cancellationToken)
	{
		List<CardEntity> cardEntities = [.. models.Select(mapper.ToEntity)];
		await repository.UpdateAsync(cardEntities, cancellationToken);
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		await repository.DeleteAsync(id, cancellationToken);
	}

	public async Task<int> GetTransactionCountByCardIdAsync(Guid cardId, CancellationToken cancellationToken)
	{
		return await repository.GetTransactionCountByCardIdAsync(cardId, cancellationToken);
	}
}
