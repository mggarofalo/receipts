using Domain.Core;

namespace Application.Interfaces.Services;

public interface IReceiptService : ISoftDeletableService<Receipt>
{
	Task<List<Receipt>> CreateAsync(List<Receipt> models, CancellationToken cancellationToken);
	Task UpdateAsync(List<Receipt> models, CancellationToken cancellationToken);
	Task<List<string>> GetDistinctLocationsAsync(string? query, int limit, CancellationToken cancellationToken);
}
