using Application.Interfaces.Services;
using Application.Models.Ynab;
using Mediator;

namespace Application.Queries.Core.Ynab;

public class GetYnabStatusQueryHandler(
	IYnabCredentialSource credentialSource,
	IYnabSyncEventService ynabSyncEventService) : IRequestHandler<GetYnabStatusQuery, YnabStatus>
{
	public async ValueTask<YnabStatus> Handle(GetYnabStatusQuery request, CancellationToken cancellationToken)
	{
		YnabCredentialState state = await credentialSource.GetStateAsync(cancellationToken);
		return await ynabSyncEventService.GetStatusAsync(state.IsUsable, cancellationToken);
	}
}
