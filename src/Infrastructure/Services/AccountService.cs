using Application.Interfaces.Services;
using Application.Models;
using Domain.Core;
using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Infrastructure.Mapping;

namespace Infrastructure.Services;

public class AccountService(IAccountRepository repository, AccountMapper mapper) : IAccountService
{
	public async Task<List<Account>> CreateAsync(List<Account> models, CancellationToken cancellationToken)
	{
		List<AccountEntity> accountEntities = [.. models.Select(mapper.ToEntity)];
		List<AccountEntity> createdAccountEntities = await repository.CreateAsync(accountEntities, cancellationToken);
		return [.. createdAccountEntities.Select(mapper.ToDomain)];
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		return await repository.ExistsAsync(id, cancellationToken);
	}

	public async Task<PagedResult<Account>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken)
	{
		int total = await repository.GetCountAsync(cancellationToken);
		List<AccountEntity> entities = await repository.GetAllAsync(offset, limit, sort, cancellationToken);
		List<Account> data = [.. entities.Select(mapper.ToDomain)];
		return new PagedResult<Account>(data, total, offset, limit);
	}

	public async Task<PagedResult<Account>> GetAllAsync(int offset, int limit, SortParams sort, bool? isActive, CancellationToken cancellationToken)
	{
		int total = await repository.GetCountAsync(cancellationToken, isActive);
		List<AccountEntity> entities = await repository.GetAllAsync(offset, limit, sort, cancellationToken, isActive);
		List<Account> data = [.. entities.Select(mapper.ToDomain)];
		return new PagedResult<Account>(data, total, offset, limit);
	}

	public async Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		AccountEntity? accountEntity = await repository.GetByIdAsync(id, cancellationToken);
		return accountEntity == null ? null : mapper.ToDomain(accountEntity);
	}

	public async Task<Account?> GetByTransactionIdAsync(Guid transactionId, CancellationToken cancellationToken)
	{
		AccountEntity? accountEntity = await repository.GetByTransactionIdAsync(transactionId, cancellationToken);
		return accountEntity == null ? null : mapper.ToDomain(accountEntity);
	}

	public async Task<int> GetCountAsync(CancellationToken cancellationToken)
	{
		return await repository.GetCountAsync(cancellationToken);
	}

	public async Task UpdateAsync(List<Account> models, CancellationToken cancellationToken)
	{
		List<AccountEntity> accountEntities = [.. models.Select(mapper.ToEntity)];
		await repository.UpdateAsync(accountEntities, cancellationToken);
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		await repository.DeleteAsync(id, cancellationToken);
	}

	public async Task<int> GetCardCountByAccountIdAsync(Guid accountId, CancellationToken cancellationToken)
	{
		return await repository.GetCardCountByAccountIdAsync(accountId, cancellationToken);
	}

	public async Task<int> GetTransactionCountByAccountIdAsync(Guid accountId, CancellationToken cancellationToken)
	{
		return await repository.GetTransactionCountByAccountIdAsync(accountId, cancellationToken);
	}
}
