using Application.Models;
using Domain.Core;

namespace Application.Interfaces.Services;

public interface ICardService : IService<Card>
{
	Task<PagedResult<Card>> GetAllAsync(int offset, int limit, SortParams sort, bool? isActive, string? q, CancellationToken cancellationToken);
	Task<Card?> GetByTransactionIdAsync(Guid transactionId, CancellationToken cancellationToken);
	Task<List<Card>> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken);
	Task<List<Card>> CreateAsync(List<Card> models, CancellationToken cancellationToken);
	Task UpdateAsync(List<Card> models, CancellationToken cancellationToken);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken);
	Task<int> GetTransactionCountByCardIdAsync(Guid cardId, CancellationToken cancellationToken);
}
