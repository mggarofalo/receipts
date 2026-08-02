using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Reports;

public class UnacceptDuplicateGroupCommandHandler(IReportService reportService)
	: IRequestHandler<UnacceptDuplicateGroupCommand, int>
{
	public async ValueTask<int> Handle(UnacceptDuplicateGroupCommand request, CancellationToken cancellationToken)
	{
		return await reportService.UnacceptDuplicateGroupAsync(request.ReceiptIds, cancellationToken);
	}
}
