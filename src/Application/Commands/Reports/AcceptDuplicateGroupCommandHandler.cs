using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Reports;

public class AcceptDuplicateGroupCommandHandler(IReportService reportService)
	: IRequestHandler<AcceptDuplicateGroupCommand, int>
{
	public async ValueTask<int> Handle(AcceptDuplicateGroupCommand request, CancellationToken cancellationToken)
	{
		return await reportService.AcceptDuplicateGroupAsync(request.ReceiptIds, cancellationToken);
	}
}
