using Application.Interfaces;
using Application.Models.Reports;

namespace Application.Queries.Aggregates.Reports;

public record GetDuplicateDetectionReportQuery(
	string MatchOn,
	string LocationTolerance,
	decimal TotalTolerance,
	bool IncludeAccepted = false) : IQuery<DuplicateDetectionResult>;
