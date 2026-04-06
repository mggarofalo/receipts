namespace Infrastructure.Interfaces.Repositories;

public interface IYnabServerKnowledgeRepository
{
	Task<long?> GetAsync(string budgetId, CancellationToken cancellationToken);
	Task UpsertAsync(string budgetId, long serverKnowledge, CancellationToken cancellationToken);
}
