using Application.Models;
using Domain.Core;

namespace Application.Interfaces.Services;

public interface IAccountService : IService<Account>
{
	Task<PagedResult<Account>> GetAllAsync(int offset, int limit, SortParams sort, bool? isActive, string? q, CancellationToken cancellationToken);
	Task<Account?> GetByTransactionIdAsync(Guid transactionId, CancellationToken cancellationToken);
	Task<List<Account>> CreateAsync(List<Account> models, CancellationToken cancellationToken);
	Task UpdateAsync(List<Account> models, CancellationToken cancellationToken);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken);
	Task<int> GetCardCountByAccountIdAsync(Guid accountId, CancellationToken cancellationToken);
	Task<int> GetTransactionCountByAccountIdAsync(Guid accountId, CancellationToken cancellationToken);
}
