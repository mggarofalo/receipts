using Application.Interfaces.Services;
using Application.Models.Reports;
using Mediator;

namespace Application.Queries.Aggregates.Reports;

public class GetDuplicateDetectionReportQueryHandler(IReportService reportService)
	: IRequestHandler<GetDuplicateDetectionReportQuery, DuplicateDetectionResult>
{
	public async ValueTask<DuplicateDetectionResult> Handle(GetDuplicateDetectionReportQuery request, CancellationToken cancellationToken)
	{
		return await reportService.GetDuplicatesAsync(
			request.MatchOn,
			request.LocationTolerance,
			request.TotalTolerance,
			request.IncludeAccepted,
			cancellationToken);
	}
}
