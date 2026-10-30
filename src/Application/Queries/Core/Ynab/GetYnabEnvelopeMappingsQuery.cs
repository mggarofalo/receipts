using Application.Interfaces;
using Application.Models.Ynab;

namespace Application.Queries.Core.Ynab;

public record GetYnabEnvelopeMappingsQuery : IQuery<List<YnabEnvelopeMappingDto>>;
