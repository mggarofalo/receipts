using Application.Interfaces;
using Application.Models.Reports;

namespace Application.Queries.Aggregates.Reports;

public record GetAcceptedDuplicatesQuery : IQuery<AcceptedDuplicatesResult>;
