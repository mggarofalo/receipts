using Application.Interfaces;
using Application.Models;

namespace Application.Queries.Core.Subcategory;

public record GetAllSubcategoriesQuery(int Offset, int Limit, SortParams Sort, bool? IsActive = null, string? Q = null) : IQuery<PagedResult<Domain.Core.Subcategory>>;
