using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class YnabServerKnowledgeRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : IYnabServerKnowledgeRepository
{
	public async Task<long?> GetAsync(string budgetId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabServerKnowledgeEntity? entity = await context.YnabServerKnowledge.FindAsync([budgetId], cancellationToken);
		return entity?.ServerKnowledge;
	}

	public async Task UpsertAsync(string budgetId, long serverKnowledge, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabServerKnowledgeEntity? existing = await context.YnabServerKnowledge.FindAsync([budgetId], cancellationToken);

		if (existing is not null)
		{
			existing.ServerKnowledge = serverKnowledge;
			existing.UpdatedAt = DateTimeOffset.UtcNow;
		}
		else
		{
			context.YnabServerKnowledge.Add(new YnabServerKnowledgeEntity
			{
				BudgetId = budgetId,
				ServerKnowledge = serverKnowledge,
				UpdatedAt = DateTimeOffset.UtcNow,
			});
		}

		await context.SaveChangesAsync(cancellationToken);
	}
}
