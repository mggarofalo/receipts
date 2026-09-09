using Infrastructure.Entities.Core;
using Infrastructure.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class YnabBudgetSelectionRepository(IDbContextFactory<ApplicationDbContext> contextFactory) : IYnabBudgetSelectionRepository
{
	public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");

	public async Task<string?> GetSelectedBudgetIdAsync(CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabSelectedBudgetEntity? entity = await context.YnabSelectedBudgets.FindAsync([SingletonId], cancellationToken);
		return entity?.BudgetId;
	}

	public async Task<bool> SetSelectedBudgetIdAsync(string budgetId, CancellationToken cancellationToken)
	{
		using ApplicationDbContext context = contextFactory.CreateDbContext();
		YnabSelectedBudgetEntity? existing = await context.YnabSelectedBudgets.FindAsync([SingletonId], cancellationToken);

		if (existing is not null)
		{
			if (string.Equals(existing.BudgetId, budgetId, StringComparison.Ordinal))
			{
				return false;
			}

			existing.BudgetId = budgetId;
			existing.UpdatedAt = DateTimeOffset.UtcNow;
		}
		else
		{
			context.YnabSelectedBudgets.Add(new YnabSelectedBudgetEntity
			{
				Id = SingletonId,
				BudgetId = budgetId,
				UpdatedAt = DateTimeOffset.UtcNow,
			});
		}

		return await context.SaveChangesAsync(cancellationToken) > 0;
	}
}
