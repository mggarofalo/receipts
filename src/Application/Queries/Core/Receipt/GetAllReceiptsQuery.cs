using Application.Interfaces;
using Application.Models;

namespace Application.Queries.Core.Receipt;

public record GetAllReceiptsQuery(int Offset, int Limit, SortParams Sort, Guid? AccountId = null, Guid? CardId = null, string? Q = null, string? Location = null) : IQuery<PagedResult<Domain.Core.Receipt>>;
