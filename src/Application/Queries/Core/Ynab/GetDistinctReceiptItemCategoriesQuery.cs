using Application.Interfaces;

namespace Application.Queries.Core.Ynab;

public record GetDistinctReceiptItemCategoriesQuery : IQuery<List<string>>;
