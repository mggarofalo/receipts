using Application.Interfaces;

namespace Application.Queries.Core.Ynab;

public record GetUnmappedCategoriesQuery : IQuery<List<string>>;
