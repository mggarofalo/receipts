using System.Linq.Expressions;
using Application.Models;
using Infrastructure.Entities.Core;
using Infrastructure.Extensions;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class CardRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : ICardRepository
{
	private static readonly Dictionary<string, Expression<Func<CardEntity, object>>> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
	{
		["cardCode"] = e => e.CardCode,
		["name"] = e => e.Name,
		["isActive"] = e => e.IsActive,
	};

	public async Task<CardEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Cards.FindAsync([id], cancellationToken);
	}

	public async Task<CardEntity?> GetByTransactionIdAsync(Guid transactionId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		Guid? cardId = await context.Transactions
			.Where(t => t.Id == transactionId)
			.Select(t => (Guid?)t.CardId)
			.FirstOrDefaultAsync(cancellationToken);

		return cardId.HasValue
			? await context.Cards.FindAsync([cardId.Value], cancellationToken)
			: null;
	}

	public async Task<List<CardEntity>> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Cards
			.AsNoTracking()
			.Where(c => c.AccountId == accountId)
			.OrderBy(c => c.Name)
			.ToListAsync(cancellationToken);
	}

	public async Task<List<CardEntity>> GetAllAsync(int offset, int limit, SortParams sort, CancellationToken cancellationToken, bool? isActive = null, string? q = null)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<CardEntity> query = context.Cards.AsNoTracking();
		if (isActive.HasValue)
		{
			query = query.Where(e => e.IsActive == isActive.Value);
		}
		query = ApplySearchFilter(query, q);

		return await query
			.ApplySort(sort, AllowedSortColumns, e => e.Name, e => e.Id)
			.Skip(offset)
			.Take(limit)
			.Select(a => new CardEntity
			{
				Id = a.Id,
				CardCode = a.CardCode,
				Name = a.Name,
				IsActive = a.IsActive,
				AccountId = a.AccountId
			})
			.ToListAsync(cancellationToken);
	}

	public async Task<List<CardEntity>> CreateAsync(List<CardEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		context.Cards.AddRange(entities);
		await context.SaveChangesAsync(cancellationToken);
		return entities;
	}

	public async Task UpdateAsync(List<CardEntity> entities, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IEnumerable<Guid> ids = entities.Select(e => e.Id);
		List<CardEntity> existingEntities = await context.Cards
			.Where(e => ids.Contains(e.Id))
			.ToListAsync(cancellationToken);

		foreach (CardEntity entity in entities)
		{
			CardEntity existingEntity = existingEntities.Single(e => e.Id == entity.Id);
			context.Entry(existingEntity).CurrentValues.SetValues(entity);
		}

		await context.SaveChangesAsync(cancellationToken);
	}

	public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Cards.AnyAsync(e => e.Id == id, cancellationToken);
	}

	public async Task<int> GetCountAsync(CancellationToken cancellationToken, bool? isActive = null, string? q = null)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		IQueryable<CardEntity> query = context.Cards;
		if (isActive.HasValue)
		{
			query = query.Where(e => e.IsActive == isActive.Value);
		}
		query = ApplySearchFilter(query, q);

		return await query.CountAsync(cancellationToken);
	}

	private static IQueryable<CardEntity> ApplySearchFilter(IQueryable<CardEntity> query, string? q)
	{
		if (string.IsNullOrWhiteSpace(q))
		{
			return query;
		}
		string search = q.Trim().ToLower();
		return query.Where(e => e.Name.ToLower().Contains(search) || e.CardCode.ToLower().Contains(search));
	}

	public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		CardEntity? entity = await context.Cards.FindAsync([id], cancellationToken);
		if (entity != null)
		{
			context.Cards.Remove(entity);
			await context.SaveChangesAsync(cancellationToken);
		}
	}

	public async Task<int> GetTransactionCountByCardIdAsync(Guid cardId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		return await context.Transactions
			.IgnoreQueryFilters()
			.CountAsync(t => t.CardId == cardId, cancellationToken);
	}
}
