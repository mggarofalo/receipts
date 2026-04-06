using Application.Interfaces.Services;
using Application.Models.Ynab;
using MediatR;

namespace Application.Queries.Core.Ynab;

public class GetYnabConnectionStatusQueryHandler(
	IYnabApiClient ynabApiClient,
	IYnabSyncRecordService syncRecordService) : IRequestHandler<GetYnabConnectionStatusQuery, YnabConnectionStatus>
{
	public async Task<YnabConnectionStatus> Handle(GetYnabConnectionStatusQuery request, CancellationToken cancellationToken)
	{
		bool isConfigured = ynabApiClient.IsConfigured;

		if (!isConfigured)
		{
			return new YnabConnectionStatus(false, false, null);
		}

		bool isConnected;
		try
		{
			await ynabApiClient.GetBudgetsAsync(cancellationToken);
			isConnected = true;
		}
		catch (Exception)
		{
			isConnected = false;
		}

		DateTimeOffset? lastSync = await syncRecordService.GetLatestSuccessfulSyncTimestampAsync(cancellationToken);

		return new YnabConnectionStatus(isConfigured, isConnected, lastSync);
	}
}
