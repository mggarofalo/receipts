using Application.Models;
using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface ICardRepository
{
	Task<CardEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<CardEntity?> GetByTransactionIdAsync(Guid transactionId, CancellationToken cancellationToken);
	Task<List<CardEntity>> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken);
	Task<List<CardEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken, bool? isActive = null, string? q = null);
	Task<List<CardEntity>> CreateAsync(List<CardEntity> entities, CancellationToken cancellationToken);
	Task UpdateAsync(List<CardEntity> entities, CancellationToken cancellationToken);
	Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);
	Task<int> GetCountAsync(CancellationToken cancellationToken, bool? isActive = null, string? q = null);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken);
	Task<int> GetTransactionCountByCardIdAsync(Guid cardId, CancellationToken cancellationToken);
}
