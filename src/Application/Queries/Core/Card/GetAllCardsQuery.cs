using Application.Interfaces;
using Application.Models;

namespace Application.Queries.Core.Card;

public record GetAllCardsQuery(int Offset, int Limit, SortParams Sort, bool? IsActive = null, string? Q = null) : IQuery<PagedResult<Domain.Core.Card>>;
