using Application.Interfaces;
using Application.Models.Ynab;

namespace Application.Queries.Core.Ynab;

public record GetAllYnabCategoryMappingsQuery : IQuery<List<YnabCategoryMappingDto>>;
