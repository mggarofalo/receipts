using Application.Interfaces;
using Application.Models;

namespace Application.Queries.Core.ItemTemplate;

// Q is an optional name filter (RECEIPTS-930). Null or blank means the unfiltered list, so every
// existing caller keeps its behaviour without passing anything.
public record GetAllItemTemplatesQuery(int Offset, int Limit, SortParams Sort, string? Q = null) : IQuery<PagedResult<Domain.Core.ItemTemplate>>;
