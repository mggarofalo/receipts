using System.Linq.Expressions;
using Application.Models;
using Infrastructure.Entities.Core;
using Infrastructure.Extensions;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class AccountRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : IAccountRepository
{
	private static readonly Dictionary<string, Expression<Func<AccountEntity, object>>> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
	{
		["name"] = e => e.Name,
		["isActive"] = e => e.IsActive,
	};

	public async Task<AccountEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Accounts.FindAsync([id], cancellationToken);
	}

	public async Task<AccountEntity?> GetByTransactionIdAsync(Guid transactionId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Transactions
			.Where(t => t.Id == transactionId)
			.Select(t => t.Account)
			.FirstOrDefaultAsync(cancellationToken);
	}

	public async Task<List<AccountEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken, bool? isActive = null, string? q = null)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<AccountEntity> query = context.Accounts.AsNoTracking();
		if (isActive.HasValue)
		{
			query = query.Where(e => e.IsActive == isActive.Value);
		}
		query = ApplySearchFilter(query, q);

		return await query
			.ApplySort(sort, AllowedSortColumns, e => e.Name, e => e.Id)
			.Skip(offset)
			.Take(limit)
			.Select(a => new AccountEntity
			{
				Id = a.Id,
				Name = a.Name,
				IsActive = a.IsActive
			})
			.ToListAsync(cancellationToken);
	}

	public async Task<List<AccountEntity>> CreateAsync(List<AccountEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		context.Accounts.AddRange(entities);
		await context.SaveChangesAsync(cancellationToken);
		return entities;
	}

	public async Task UpdateAsync(List<AccountEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IEnumerable<Guid> ids = entities.Select(e => e.Id);
		List<AccountEntity> existingEntities = await context.Accounts
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		foreach (AccountEntity entity in entities)
		{
			AccountEntity existingEntity = existingEntities.Single(e => e.Id == entity.Id);
			context.Entry(existingEntity).CurrentValues.SetValues(entity);
		}

		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Accounts.AnyAsync(e => e.Id == id, cancellationToken);
	}

	public async Task<int> GetCountAsync(CancellationToken cancellationToken, bool? isActive = null, string? q = null)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<AccountEntity> query = context.Accounts;
		if (isActive.HasValue)
		{
			query = query.Where(e => e.IsActive == isActive.Value);
		}
		query = ApplySearchFilter(query, q);

		return await query.CountAsync(cancellationToken);
	}

	private static IQueryable<AccountEntity> ApplySearchFilter(IQueryable<AccountEntity> query, string? q)
	{
		if (string.IsNullOrWhiteSpace(q))
		{
			return query;
		}
		string search = q.Trim().ToLower();
		return query.Where(e => e.Name.ToLower().Contains(search));
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		AccountEntity? entity = await context.Accounts.FindAsync([id], cancellationToken);
		if (entity != null)
		{
			context.Accounts.Remove(entity);
			await context.SaveChangesAsync(cancellationToken);
		}
	}

	public async Task<int> GetCardCountByAccountIdAsync(Guid accountId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Cards
			.CountAsync(c => c.AccountId == accountId, cancellationToken);
	}

	public async Task<int> GetTransactionCountByAccountIdAsync(Guid accountId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		// IgnoreQueryFilters() so soft-deleted (trashed) transactions count too — the
		// Transactions.AccountId FK is Restrict, and a hard account delete would fail (or,
		// worse, historically cascade-destroy) rows the soft-delete filter would hide.
		return await context.Transactions
			.IgnoreQueryFilters()
			.CountAsync(t => t.AccountId == accountId, cancellationToken);
	}
}
