using Application.Interfaces;
using Application.Models.Ynab;

namespace Application.Queries.Core.Ynab;

public record GetStaleMappingsQuery : IQuery<StaleMappingsResult>;
