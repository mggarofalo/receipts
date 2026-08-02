using Application.Interfaces.Services;
using Application.Models.Reports;
using Mediator;

namespace Application.Queries.Aggregates.Reports;

public class GetAcceptedDuplicatesQueryHandler(IReportService reportService)
	: IRequestHandler<GetAcceptedDuplicatesQuery, AcceptedDuplicatesResult>
{
	public async ValueTask<AcceptedDuplicatesResult> Handle(GetAcceptedDuplicatesQuery request, CancellationToken cancellationToken)
	{
		return await reportService.GetAcceptedDuplicatesAsync(cancellationToken);
	}
}
