using Application.Interfaces;
using Application.Models.Search;

namespace Application.Queries.Search;

public record SearchPurchasesQuery(string Q, int Offset = 0, int Limit = 25,
	PurchaseSearchOptions? Options = null) : IQuery<PurchaseSearchResult>;
