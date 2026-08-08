using Application.Models;
using Domain.Core;

namespace Application.Interfaces.Services;

public interface IItemTemplateService : ISoftDeletableService<ItemTemplate>
{
	Task<List<ItemTemplate>> CreateAsync(List<ItemTemplate> models, CancellationToken cancellationToken);
	Task UpdateAsync(List<ItemTemplate> models, CancellationToken cancellationToken);

	// RECEIPTS-930. Name search. Declared here rather than widening ISoftDeletableService.GetAllAsync
	// with a `q`, which would oblige every other entity's service and every one of their callers to
	// carry a parameter only templates need.
	Task<PagedResult<ItemTemplate>> SearchAsync(string? q, int offset, int limit, SortParams sort, CancellationToken cancellationToken);
}
