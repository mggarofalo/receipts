using Application.Models;
using Infrastructure.Entities.Core;

namespace Infrastructure.Interfaces.Repositories;

public interface IAccountRepository
{
	Task<AccountEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
	Task<AccountEntity?> GetByTransactionIdAsync(Guid transactionId, CancellationToken cancellationToken);
	Task<List<AccountEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken, bool? isActive = null, string? q = null);
	Task<List<AccountEntity>> CreateAsync(List<AccountEntity> entities, CancellationToken cancellationToken);
	Task UpdateAsync(List<AccountEntity> entities, CancellationToken cancellationToken);
	Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);
	Task<int> GetCountAsync(CancellationToken cancellationToken, bool? isActive = null, string? q = null);
	Task DeleteAsync(Guid id, CancellationToken cancellationToken);
	Task<int> GetCardCountByAccountIdAsync(Guid accountId, CancellationToken cancellationToken);
	Task<int> GetTransactionCountByAccountIdAsync(Guid accountId, CancellationToken cancellationToken);
}
