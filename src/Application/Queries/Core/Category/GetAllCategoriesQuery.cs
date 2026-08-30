using Application.Interfaces;
using Application.Models;

namespace Application.Queries.Core.Category;

public record GetAllCategoriesQuery(int Offset, int Limit, SortParams Sort, bool? IsActive = null, string? Q = null) : IQuery<PagedResult<Domain.Core.Category>>;
